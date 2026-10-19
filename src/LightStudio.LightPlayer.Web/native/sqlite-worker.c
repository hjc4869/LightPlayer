#include <emscripten.h>
#include <sqlite3.h>
#include <stdint.h>
#include <string.h>

typedef struct {
    sqlite3_file base;
    int handle;
} BrowserFile;

EM_JS(int, storage_open, (const char *name), {
    return Module.storage.open(UTF8ToString(name));
});

EM_JS(int, storage_read, (int handle, void *buffer, int length, double offset), {
    return Module.storage.read(handle, buffer, length, offset);
});

EM_JS(int, storage_write, (int handle, const void *buffer, int length, double offset), {
    return Module.storage.write(handle, buffer, length, offset);
});

EM_JS(int, storage_truncate, (int handle, double length), {
    return Module.storage.truncate(handle, length);
});

EM_JS(int, storage_sync, (int handle), {
    return Module.storage.sync(handle);
});

EM_JS(double, storage_size, (int handle), {
    return Module.storage.size(handle);
});

EM_JS(int, storage_delete, (const char *name), {
    return Module.storage.delete(UTF8ToString(name));
});

EM_JS(int, storage_exists, (const char *name), {
    return Module.storage.exists(UTF8ToString(name));
});

EM_JS(void, library_function, (sqlite3_context *context, int count, sqlite3_value **values, int kind), {
    Module.libraryFunction(context, count, values, kind);
});

EM_JS(int, library_compare, (int left_length, const void *left, int right_length, const void *right), {
    const decoder = new TextDecoder();
    return Module.libraryCompare(decoder.decode(HEAPU8.subarray(left, left + left_length)),
        decoder.decode(HEAPU8.subarray(right, right + right_length)));
});

EM_JS(int, operation_expired, (), {
    return performance.now() >= Module.deadline;
});

EM_JS(double, current_time, (), { return Date.now(); });

static int file_close(sqlite3_file *file) { return SQLITE_OK; }
static int file_read(sqlite3_file *file, void *buffer, int length, sqlite3_int64 offset) {
    return storage_read(((BrowserFile *)file)->handle, buffer, length, (double)offset);
}
static int file_write(sqlite3_file *file, const void *buffer, int length, sqlite3_int64 offset) {
    return storage_write(((BrowserFile *)file)->handle, buffer, length, (double)offset);
}
static int file_truncate(sqlite3_file *file, sqlite3_int64 length) {
    return storage_truncate(((BrowserFile *)file)->handle, (double)length);
}
static int file_sync(sqlite3_file *file, int flags) {
    return storage_sync(((BrowserFile *)file)->handle);
}
static int file_size(sqlite3_file *file, sqlite3_int64 *length) {
    double size = storage_size(((BrowserFile *)file)->handle);
    if (size < 0) return SQLITE_IOERR_FSTAT;
    *length = (sqlite3_int64)size;
    return SQLITE_OK;
}
static int file_lock(sqlite3_file *file, int level) { return SQLITE_OK; }
static int file_reserved(sqlite3_file *file, int *reserved) { *reserved = 0; return SQLITE_OK; }
static int file_control(sqlite3_file *file, int operation, void *argument) { return SQLITE_NOTFOUND; }
static int file_sector(sqlite3_file *file) { return 4096; }
static int file_characteristics(sqlite3_file *file) { return 0; }

static const sqlite3_io_methods file_methods = {
    1, file_close, file_read, file_write, file_truncate, file_sync, file_size,
    file_lock, file_lock, file_reserved, file_control, file_sector, file_characteristics
};

static int vfs_open(sqlite3_vfs *vfs, const char *name, sqlite3_file *file, int flags, int *out_flags) {
    if (!name) return SQLITE_CANTOPEN;
    int handle = storage_open(name);
    if (handle < 0) return SQLITE_CANTOPEN;
    ((BrowserFile *)file)->handle = handle;
    file->pMethods = &file_methods;
    if (out_flags) *out_flags = flags;
    return SQLITE_OK;
}
static int vfs_delete(sqlite3_vfs *vfs, const char *name, int sync_directory) { return storage_delete(name); }
static int vfs_access(sqlite3_vfs *vfs, const char *name, int flags, int *result) {
    int exists = storage_exists(name);
    if (exists < 0) return SQLITE_IOERR_ACCESS;
    *result = exists;
    return SQLITE_OK;
}
static int vfs_path(sqlite3_vfs *vfs, const char *name, int length, char *result) {
    if ((int)strlen(name) >= length) return SQLITE_CANTOPEN;
    sqlite3_snprintf(length, result, "%s", name);
    return SQLITE_OK;
}
static int vfs_random(sqlite3_vfs *vfs, int length, char *output) {
    for (int index = 0; index < length; index++) output[index] = (char)(emscripten_random() * 256);
    return length;
}
static int vfs_sleep(sqlite3_vfs *vfs, int microseconds) { return 0; }
static int vfs_time(sqlite3_vfs *vfs, double *time) { *time = current_time() / 86400000.0 + 2440587.5; return SQLITE_OK; }

static sqlite3_vfs browser_vfs = {
    .iVersion = 1, .szOsFile = sizeof(BrowserFile), .mxPathname = 512, .zName = "lightplayer-opfs",
    .xOpen = vfs_open, .xDelete = vfs_delete, .xAccess = vfs_access, .xFullPathname = vfs_path,
    .xRandomness = vfs_random, .xSleep = vfs_sleep, .xCurrentTime = vfs_time
};

static void scalar(sqlite3_context *context, int count, sqlite3_value **values) {
    library_function(context, count, values, (int)(intptr_t)sqlite3_user_data(context));
}
static int compare(void *state, int left_length, const void *left, int right_length, const void *right) {
    return library_compare(left_length, left, right_length, right);
}
static int progress(void *state) { return operation_expired(); }

EMSCRIPTEN_KEEPALIVE int lp_sqlite_open(const char *name, sqlite3 **database) {
    sqlite3_vfs_register(&browser_vfs, 0);
    int result = sqlite3_open_v2(name, database, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, "lightplayer-opfs");
    if (result != SQLITE_OK) return result;
    sqlite3 *opened = *database;
    sqlite3_extended_result_codes(opened, 1);
    sqlite3_busy_timeout(opened, 0);
    sqlite3_limit(opened, SQLITE_LIMIT_LENGTH, 8 * 1024 * 1024);
    sqlite3_limit(opened, SQLITE_LIMIT_SQL_LENGTH, 1024 * 1024);
    sqlite3_progress_handler(opened, 1000, progress, 0);
    const char *names[] = { "lightplayer_leading_int", "lightplayer_parent_path", "lightplayer_track_title",
        "lightplayer_video_title", "lightplayer_shuffle_key", "lightplayer_utc_ticks", "lightplayer_duration_ticks" };
    for (int index = 0; index < 7 && result == SQLITE_OK; index++)
        result = sqlite3_create_function(opened, names[index], index == 2 || index == 3 ? 2 : 1,
            SQLITE_UTF8 | SQLITE_DETERMINISTIC, (void *)(intptr_t)index, scalar, 0, 0);
    if (result == SQLITE_OK) result = sqlite3_create_collation(opened, "LIGHTPLAYER_NOCASE", SQLITE_UTF8, 0, compare);
    if (result == SQLITE_OK) result = sqlite3_exec(opened,
        "PRAGMA foreign_keys=ON;PRAGMA journal_mode=DELETE;PRAGMA synchronous=FULL;PRAGMA temp_store=MEMORY;PRAGMA cache_size=-8192;",
        0, 0, 0);
    return result;
}
