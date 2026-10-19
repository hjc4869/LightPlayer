// https://lrclib.net/api/search?artist_name={artist}&track_name={track}
// response: list of JSON objects with below schema
//      id (integer)
//      name (string)
//      trackName (string)
//      artistName (string)
//      albumName (string)
//      duration (number)
//      instrumental (boolean)
//      plainLyrics (string)
//      syncedLyrics (string)

function getSourceInfo() {
    return [
        { text: "Terms of Use", url: "https://lrclib.net/docs" }
    ];
}

const requestHeaders = {
    "Lrclib-Client": "Light Player LRCLIB Extension v1.0 (https://github.com/hjc4869/LightPlayer/blob/main/src/LightStudio.LightPlayer/Resource/lrclib.js)"
};

async function lookupLrcAsync(title, artist, signal) {
    const query = `track_name=${encodeURIComponent(title)}&artist_name=${encodeURIComponent(artist)}`;
    const response = await fetch(`https://lrclib.net/api/search?${query}`, {
        headers: requestHeaders,
        signal
    });
    if (!response.ok) {
        throw new Error(`LRCLIB request failed with status ${response.status}`);
    }

    return (await response.json()).map(item => ({
        opaque: {
            plainLyrics: item.plainLyrics,
            syncedLyrics: item.syncedLyrics
        },
        title: item.trackName || item.name,
        artist: item.artistName,
        album: item.albumName,
        duration: item.duration,
        instrumental: item.instrumental
    }));
}

async function downloadLrcAsync(lrcinfo, signal) {
    if (lrcinfo.opaque.syncedLyrics) {
        return { content: lrcinfo.opaque.syncedLyrics, type: "synced" };
    }

    return { content: lrcinfo.opaque.plainLyrics || "", type: "plain" };
}