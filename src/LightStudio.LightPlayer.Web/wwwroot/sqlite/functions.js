let ordinalCasing;

export function configureOrdinalCasing(pairs) {
    if (!Array.isArray(pairs) || pairs.length > 16384 || pairs.length % 2 ||
        pairs.some(value => !Number.isInteger(value) || value < 0 || value > 0x10ffff))
        throw new Error("Invalid SQLite ordinal casing map.");
    ordinalCasing = new Map();
    for (let index = 0; index < pairs.length; index += 2) ordinalCasing.set(pairs[index], pairs[index + 1]);
}

export function compareOrdinalIgnoreCase(left, right) {
    if (!ordinalCasing) throw new Error("SQLite ordinal casing is not configured.");
    let firstIndex = 0;
    let secondIndex = 0;
    while (firstIndex < left.length && secondIndex < right.length) {
        const firstScalar = left.codePointAt(firstIndex);
        const secondScalar = right.codePointAt(secondIndex);
        const firstUpper = ordinalCasing.get(firstScalar) ?? firstScalar;
        const secondUpper = ordinalCasing.get(secondScalar) ?? secondScalar;
        if (firstUpper !== secondUpper) return firstUpper < secondUpper ? -1 : 1;
        firstIndex += firstScalar > 0xffff ? 2 : 1;
        secondIndex += secondScalar > 0xffff ? 2 : 1;
    }
    return left.length < right.length ? -1 : left.length > right.length ? 1 : 0;
}

function parentPath(value) {
    if (value === null || value.length === 0 || /^\/+$/u.test(value)) return null;
    const end = value.lastIndexOf("/");
    return end < 0 ? "" : end === 0 ? "/" : value.slice(0, end).replace(/\/{2,}/gu, "/").replace(/\/+$/u, "") || "/";
}

function whitespace(value) {
    return value === null || /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]*$/u.test(value);
}

function trackTitle(title, path) {
    if (!whitespace(title)) return title;
    if (whitespace(path)) return "";
    const name = path.slice(path.lastIndexOf("/") + 1);
    const extension = name.lastIndexOf(".");
    return extension < 0 ? name : name.slice(0, extension);
}

function utcTicks(value) {
    if (!value) return 0n;
    const match = /^(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})$/u.exec(value);
    if (!match) throw new Error(`Invalid SQLite date value: ${value}`);
    const date = new Date(0);
    date.setUTCFullYear(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
    date.setUTCHours(Number(match[4]), Number(match[5]), Number(match[6]), 0);
    const zone = match[8];
    const offset = zone === "Z" ? 0 : (Number(zone.slice(1, 3)) * 60 + Number(zone.slice(4))) * (zone[0] === "-" ? -1 : 1);
    return BigInt(date.getTime() - offset * 60000) * 10000n + 621355968000000000n + BigInt((match[7] ?? "").padEnd(7, "0"));
}

function durationTicks(value) {
    const match = /^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$/u.exec(value);
    if (!match) throw new Error(`Invalid SQLite duration value: ${value}`);
    const seconds = ((BigInt(match[2] ?? 0) * 24n + BigInt(match[3])) * 60n + BigInt(match[4])) * 60n + BigInt(match[5]);
    return (seconds * 10000000n + BigInt((match[6] ?? "").padEnd(7, "0"))) * (match[1] ? -1n : 1n);
}

export function libraryFunction(kind, values) {
    switch (kind) {
        case 0: {
            const value = values[0] ?? "";
            let length = 0;
            while (length < value.length && length < 9 && /\p{Nd}/u.test(value[length])) length++;
            const digits = value.slice(0, length);
            return /^[0-9]+$/u.test(digits) ? BigInt(digits) : 0n;
        }
        case 1: return parentPath(values[0]);
        case 2: return trackTitle(values[0], values[1]);
        case 3: return !whitespace(values[0]) ? values[0] : values[1] ?? "";
        case 4: return BigInt.asUintN(32, BigInt(values[0]) * 2654435761n);
        case 5: return utcTicks(values[0]);
        case 6: return durationTicks(values[0]);
        default: throw new Error("Unknown library SQL function.");
    }
}

export function installFunctions(module) {
    module.libraryCompare = compareOrdinalIgnoreCase;
    module.libraryFunction = (context, count, pointers, kind) => {
        const textResult = (value, error = false) => {
            const length = module.lengthBytesUTF8(value);
            const pointer = module._malloc(length + 1);
            if (!pointer) throw new Error("SQLite function allocation failed.");
            try {
                module.stringToUTF8(value, pointer, length + 1);
                if (error) module._sqlite3_result_error(context, pointer, length);
                else module._sqlite3_result_text(context, pointer, length, -1);
            } finally { module._free(pointer); }
        };
        try {
            const values = [];
            for (let index = 0; index < count; index++) {
                const pointer = module.HEAPU32[(pointers >>> 2) + index];
                const text = module._sqlite3_value_text(pointer);
                const length = module._sqlite3_value_bytes(pointer);
                values.push(module._sqlite3_value_type(pointer) === 5 ? null : kind === 4 ?
                    module._sqlite3_value_int64(pointer) : new TextDecoder().decode(module.HEAPU8.subarray(text, text + length)));
            }
            const result = libraryFunction(kind, values);
            if (result === null) module._sqlite3_result_null(context);
            else if (typeof result === "bigint") module._sqlite3_result_int64(context, result);
            else textResult(result);
        } catch (error) { textResult(error.stack ?? String(error), true); }
    };
}
