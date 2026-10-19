const DATABASE_NAME = 'lightplayer';
const DATABASE_VERSION = 1;
const STORE_NAME = 'state';
const DEFAULT_PREFIX = 'lightplayer.';

let databasePromise;
let statePromise;
const writeChains = new Map();

function openDatabase() {
    databasePromise ??= new Promise((resolve, reject) => {
        const request = globalThis.indexedDB.open(DATABASE_NAME, DATABASE_VERSION);
        request.onupgradeneeded = () => {
            const database = request.result;
            if (!database.objectStoreNames.contains(STORE_NAME)) {
                database.createObjectStore(STORE_NAME);
            }
        };
        request.onsuccess = () => {
            const database = request.result;
            database.onversionchange = () => database.close();
            resolve(database);
        };
        request.onerror = () => reject(request.error ?? new Error('Could not open browser storage.'));
        request.onblocked = () => reject(new Error('Browser storage upgrade was blocked.'));
    });

    return databasePromise;
}

function transactionCompleted(transaction) {
    return new Promise((resolve, reject) => {
        transaction.oncomplete = () => resolve();
        transaction.onerror = () => reject(transaction.error ?? new Error('Browser storage transaction failed.'));
        transaction.onabort = () => reject(transaction.error ?? new Error('Browser storage transaction was aborted.'));
    });
}

async function readDatabase(database) {
    const entries = new Map();
    const transaction = database.transaction(STORE_NAME, 'readonly');
    const request = transaction.objectStore(STORE_NAME).openCursor();

    await new Promise((resolve, reject) => {
        request.onsuccess = () => {
            const cursor = request.result;
            if (!cursor) {
                resolve();
                return;
            }

            if (typeof cursor.key === 'string' && typeof cursor.value === 'string') {
                entries.set(cursor.key, cursor.value);
            }
            cursor.continue();
        };
        request.onerror = () => reject(request.error ?? new Error('Could not read browser storage.'));
    });

    await transactionCompleted(transaction);
    return entries;
}

function readLocalEntries(prefix) {
    const entries = [];
    for (let index = 0; index < globalThis.localStorage.length; index++) {
        const key = globalThis.localStorage.key(index);
        if (key?.startsWith(prefix)) {
            const value = globalThis.localStorage.getItem(key);
            if (value !== null) {
                entries.push([key, value]);
            }
        }
    }
    return entries;
}

async function migrateLocalStorage(database, entries, prefix) {
    const migratedKeys = [];
    const transaction = database.transaction(STORE_NAME, 'readwrite');
    const store = transaction.objectStore(STORE_NAME);

    for (const [key, value] of readLocalEntries(prefix)) {
        if (!entries.has(key)) {
            entries.set(key, value);
            store.put(value, key);
        }
        migratedKeys.push(key);
    }

    await transactionCompleted(transaction);
    for (const key of migratedKeys) {
        globalThis.localStorage.removeItem(key);
    }
}

async function loadState(prefix) {
    const database = await openDatabase();
    const entries = await readDatabase(database);
    await migrateLocalStorage(database, entries, prefix);
    return entries;
}

function ensureState(prefix = DEFAULT_PREFIX) {
    statePromise ??= loadState(prefix);
    return statePromise;
}

function enqueueWrite(key, operation) {
    const previous = writeChains.get(key) ?? Promise.resolve();
    const current = previous.catch(() => { }).then(operation);
    writeChains.set(key, current);
    current.finally(() => {
        if (writeChains.get(key) === current) {
            writeChains.delete(key);
        }
    }).catch(() => { });
    return current;
}

async function writeDatabaseValue(key, value) {
    const database = await openDatabase();
    const transaction = database.transaction(STORE_NAME, 'readwrite');
    transaction.objectStore(STORE_NAME).put(value, key);
    await transactionCompleted(transaction);
}

async function removeDatabaseValue(key) {
    const database = await openDatabase();
    const transaction = database.transaction(STORE_NAME, 'readwrite');
    transaction.objectStore(STORE_NAME).delete(key);
    await transactionCompleted(transaction);
}

export async function initialize(prefix = DEFAULT_PREFIX) {
    const entries = await ensureState(prefix);

    return JSON.stringify({
        origin: globalThis.location.origin,
        entries: Object.fromEntries(entries),
    });
}

export async function getItem(key) {
    const entries = await ensureState();
    return entries.get(key) ?? null;
}

export async function setItem(key, value) {
    const entries = await ensureState();
    entries.set(key, value);

    try {
        await enqueueWrite(key, () => writeDatabaseValue(key, value));
        return true;
    } catch (error) {
        console.error(`Could not persist browser state '${key}'.`, error);
        return false;
    }
}

export async function removeItem(key) {
    const entries = await ensureState();
    entries.delete(key);

    try {
        await enqueueWrite(key, () => removeDatabaseValue(key));
        return true;
    } catch (error) {
        console.error(`Could not remove browser state '${key}'.`, error);
        return false;
    }
}
