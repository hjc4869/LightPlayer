// https://music.163.com/api/search/get/web?s={keyword}&type=1&offset=0&total=true&limit=20
// response: { result: { songs: [ { id, name, duration (ms), artists: [{ name }], album: { name } } ] } }
//
// https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1
// response: { lrc: { lyric }, klyric: { lyric }, tlyric: { lyric } }

// Set to true to merge the Chinese translation into the original lyric lines.
const INCLUDE_TRANSLATION = false;

function getSourceInfo() {
    return [
        {
            text: "Terms of Use",
            url: "https://developer.music.163.com/st/developer/document"
        }
    ];
}

const requestHeaders = {
    "Referer": "https://music.163.com",
    "Cookie": "appver=1.5.0.75771"
};

const timestampPattern = /\[(\d+):(\d+)(?:[.:](\d+))?\]/g;

async function getJsonAsync(url, signal) {
    const response = await fetch(url, {
        headers: requestHeaders,
        signal
    });
    if (!response.ok) {
        throw new Error(`NetEase request failed with status ${response.status}`);
    }

    return response.json();
}

function isSynced(lyric) {
    return /\[\d+:\d+(?:[.:]\d+)?\]/.test(lyric);
}

function timestampKey(minute, second, fraction) {
    let milliseconds = 0;
    if (fraction) {
        milliseconds = Number((fraction + "000").slice(0, 3));
    }
    return Number(minute) * 60000 + Number(second) * 1000 + milliseconds;
}

function buildTranslationMap(translation) {
    const map = {};
    for (const line of translation.split(/\r?\n/)) {
        timestampPattern.lastIndex = 0;
        const text = line.replace(timestampPattern, "").trim();
        if (!text) {
            continue;
        }

        timestampPattern.lastIndex = 0;
        let match;
        while ((match = timestampPattern.exec(line)) !== null) {
            map[timestampKey(match[1], match[2], match[3])] = text;
        }
    }
    return map;
}

function mergeTranslation(lyric, translation) {
    if (!lyric || !translation || !isSynced(lyric) || !isSynced(translation)) {
        return lyric;
    }

    const map = buildTranslationMap(translation);
    return lyric.split(/\r?\n/).map(line => {
        timestampPattern.lastIndex = 0;
        const match = timestampPattern.exec(line);
        if (!match) {
            return line;
        }

        timestampPattern.lastIndex = 0;
        const text = line.replace(timestampPattern, "").trim();
        const translated = map[timestampKey(match[1], match[2], match[3])];
        if (!text || !translated || translated === text) {
            return line;
        }

        return `${line.trimEnd()} （${translated}）`;
    }).join("\n");
}

async function lookupLrcAsync(title, artist, signal) {
    if (!title || !title.trim()) {
        return [];
    }

    const keyword = artist && artist.trim() ? `${title} ${artist}` : title;
    const query = `s=${encodeURIComponent(keyword)}&type=1&offset=0&total=true&limit=20`;
    const result = (await getJsonAsync(
        `https://music.163.com/api/search/get/web?${query}`,
        signal)).result;
    const songs = (result && result.songs) || [];
    return songs.map(song => ({
        opaque: { id: song.id },
        title: song.name,
        artist: (song.artists || []).map(a => a.name).join(", "),
        album: song.album ? song.album.name : "",
        duration: (song.duration || 0) / 1000,
        instrumental: false
    }));
}

async function downloadLrcAsync(lrcInfo, signal) {
    const id = lrcInfo.opaque && lrcInfo.opaque.id;
    if (!id) {
        return { content: "", type: "plain" };
    }

    const lyric = await getJsonAsync(
        `https://music.163.com/api/song/lyric?id=${encodeURIComponent(id)}&lv=1&kv=1&tv=-1`,
        signal);

    let content = (lyric.lrc && lyric.lrc.lyric) || "";
    if (INCLUDE_TRANSLATION) {
        content = mergeTranslation(content, (lyric.tlyric && lyric.tlyric.lyric) || "");
    }
    return { content: content, type: isSynced(content) ? "synced" : "plain" };
}
