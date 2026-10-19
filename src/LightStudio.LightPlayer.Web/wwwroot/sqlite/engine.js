import { installFunctions } from "./functions.js";

const maximumResultBytes = 1024 * 1024;
const maximumRows = 128;

export class SqliteEngine {
    constructor(module) {
        this.module = module;
        installFunctions(module);
    }

    allocateText(value) {
        const length = this.module.lengthBytesUTF8(value);
        const pointer = this.module._malloc(length + 1);
        if (!pointer) throw new Error("SQLite allocation failed.");
        this.module.stringToUTF8(value, pointer, length + 1);
        return pointer;
    }

    check(result, database) {
        if (result === 0) return;
        const nativeMessage = database ? this.module.UTF8ToString(this.module._sqlite3_errmsg(database)) : "SQLite operation failed.";
        const cause = this.module.storage?.lastError;
        if (this.module.storage) this.module.storage.lastError = null;
        const error = new Error(cause ? `${nativeMessage}\n${cause.stack ?? cause}` : nativeMessage);
        error.code = result;
        throw error;
    }

    budget() { this.module.deadline = performance.now() + 10000; }

    open(name) {
        this.budget();
        const text = this.allocateText(name);
        const output = this.module._malloc(4);
        let database = 0;
        try {
            this.module.HEAPU32[output >>> 2] = 0;
            const result = this.module._lp_sqlite_open(text, output);
            database = this.module.HEAPU32[output >>> 2];
            this.check(result, database);
            return database;
        } catch (error) {
            if (database) this.module._sqlite3_close(database);
            throw error;
        } finally { this.module._free(text); this.module._free(output); }
    }

    close(database) { this.check(this.module._sqlite3_close(database), database); }

    bind(statement, parameters, database) {
        for (const parameter of parameters ?? []) {
            const name = this.allocateText(parameter.name);
            let ordinal;
            try { ordinal = this.module._sqlite3_bind_parameter_index(statement, name); }
            finally { this.module._free(name); }
            if (!ordinal) continue;
            const value = parameter.value;
            let result;
            switch (value.kind) {
                case 5: result = this.module._sqlite3_bind_null(statement, ordinal); break;
                case 1: {
                    const integer = BigInt(value.text);
                    if (BigInt.asIntN(64, integer) !== integer) throw new Error("SQLite integer exceeds Int64.");
                    result = this.module._sqlite3_bind_int64(statement, ordinal, integer);
                    break;
                }
                case 2: {
                    const number = Number(value.text);
                    if (!Number.isFinite(number)) throw new Error("SQLite real must be finite.");
                    result = this.module._sqlite3_bind_double(statement, ordinal, number);
                    break;
                }
                case 3: {
                    const pointer = this.allocateText(value.text);
                    try { result = this.module._sqlite3_bind_text(statement, ordinal, pointer, this.module.lengthBytesUTF8(value.text), -1); }
                    finally { this.module._free(pointer); }
                    break;
                }
                case 4: {
                    const bytes = Uint8Array.from(atob(value.text), character => character.charCodeAt(0));
                    const pointer = this.module._malloc(Math.max(1, bytes.length));
                    if (!pointer) throw new Error("SQLite parameter allocation failed.");
                    try {
                        this.module.HEAPU8.set(bytes, pointer);
                        result = this.module._sqlite3_bind_blob(statement, ordinal, pointer, bytes.length, -1);
                    } finally { this.module._free(pointer); }
                    break;
                }
                default: throw new Error("Invalid SQLite parameter type.");
            }
            this.check(result, database);
        }
    }

    value(statement, column) {
        const kind = this.module._sqlite3_column_type(statement, column);
        let text = null;
        switch (kind) {
            case 1: text = this.module._sqlite3_column_int64(statement, column).toString(); break;
            case 2: text = this.module._sqlite3_column_double(statement, column).toString(); break;
            case 3: {
                const pointer = this.module._sqlite3_column_text(statement, column);
                const bytes = this.module._sqlite3_column_bytes(statement, column);
                if (bytes > maximumResultBytes) throw new Error("SQLite text exceeds the result budget.");
                text = new TextDecoder().decode(this.module.HEAPU8.subarray(pointer, pointer + bytes));
                break;
            }
            case 4: {
                const pointer = this.module._sqlite3_column_blob(statement, column);
                const bytes = this.module._sqlite3_column_bytes(statement, column);
                if (bytes > maximumResultBytes / 2) throw new Error("SQLite blob exceeds the result budget.");
                let encoded = "";
                for (let index = 0; index < bytes; index += 8192)
                    encoded += String.fromCharCode(...this.module.HEAPU8.subarray(pointer + index, pointer + Math.min(bytes, index + 8192)));
                text = btoa(encoded);
                break;
            }
            case 5: break;
            default: throw new Error("Invalid SQLite result type.");
        }
        return { kind, text };
    }

    execute(database, sql, parameters = [], mode = "execute") {
        this.budget();
        if (typeof sql !== "string" || sql.length > 1024 * 1024 || parameters.length > 4096) throw new Error("SQLite command exceeds its budget.");
        const source = this.allocateText(sql);
        const output = this.module._malloc(8);
        let position = source;
        let statement = 0;
        let scalar = null;
        let changes = 0;
        try {
            while (this.module.HEAPU8[position]) {
                this.module.HEAPU32[output >>> 2] = 0;
                this.check(this.module._sqlite3_prepare_v2(database, position, -1, output, output + 4), database);
                statement = this.module.HEAPU32[output >>> 2];
                const next = this.module.HEAPU32[(output + 4) >>> 2];
                if (next <= position) throw new Error("SQLite parser did not advance.");
                position = next;
                if (!statement) continue;
                this.bind(statement, parameters, database);
                const columns = this.module._sqlite3_column_count(statement);
                if (mode === "reader" && columns > 0) {
                    const cursor = { statement, database, pending: null, closed: false };
                    statement = 0;
                    return cursor;
                }
                let result;
                while ((result = this.module._sqlite3_step(statement)) === 100) {
                    if (mode === "scalar" && scalar === null) scalar = this.value(statement, 0);
                }
                this.check(result === 101 ? 0 : result, database);
                if (columns === 0) changes += this.module._sqlite3_changes(database);
                const completed = statement;
                statement = 0;
                this.check(this.module._sqlite3_finalize(completed), database);
            }
            return mode === "scalar" ? { value: scalar } : mode === "reader" ? { statement: 0, database, closed: true } : { changes };
        } finally {
            if (statement) this.module._sqlite3_finalize(statement);
            this.module._free(source);
            this.module._free(output);
        }
    }

    read(cursor) {
        this.budget();
        const rows = [];
        let bytes = 0;
        try {
            while (!cursor.closed && rows.length < maximumRows) {
                let row = cursor.pending;
                cursor.pending = null;
                if (!row) {
                    const result = this.module._sqlite3_step(cursor.statement);
                    if (result === 101) { this.finish(cursor); break; }
                    this.check(result === 100 ? 0 : result, cursor.database);
                    row = Array.from({ length: this.module._sqlite3_column_count(cursor.statement) }, (_, column) => this.value(cursor.statement, column));
                }
                const size = JSON.stringify(row).length * 2;
                if (size > maximumResultBytes) throw new Error("SQLite row exceeds the result budget.");
                if (bytes + size > maximumResultBytes) { cursor.pending = row; break; }
                bytes += size;
                rows.push(row);
            }
            return { rows, done: cursor.closed };
        } catch (error) { this.finish(cursor); throw error; }
    }

    finish(cursor) {
        if (cursor.closed) return;
        cursor.closed = true;
        cursor.pending = null;
        const statement = cursor.statement;
        cursor.statement = 0;
        this.module._sqlite3_finalize(statement);
    }

    inTransaction(database) { return !this.module._sqlite3_get_autocommit(database); }
}
