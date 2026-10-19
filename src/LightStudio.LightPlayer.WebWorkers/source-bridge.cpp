#include <emscripten.h>
#include <emscripten/threading.h>
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#include <mutex>

extern "C" int lightplayer_completion_wait(volatile int* word, int expected, int timeout_milliseconds) {
    int result = emscripten_futex_wait(word, (uint32_t)expected, (double)timeout_milliseconds);
    if (result == 0) return 0;
    if (result == -EWOULDBLOCK) return 1;
    if (result == -ETIMEDOUT) return 2;
    return result;
}

namespace {
constexpr int pending = 1, writing = 2, ready = 3, failed = 4, abandoned = 5, canceled = 6;
constexpr int state_mask = 7, sequence_shift = 3;
constexpr uint32_t sequence_mask = 0x0fffffff;
constexpr int wait_timeout = 30000, retire_slice = 1000;
constexpr int av_error_eof = -541478725, av_seek_size = 0x10000;

struct Slot {
    int64_t start;
    int requested;
    uint32_t sequence;
    uint64_t last_use;
};

struct Bridge {
    int source;
    int window_size;
    int slot_count;
    int prefetch_windows;
    int64_t length;
    int64_t position;
    int64_t sequential_end;
    uint64_t use_clock;
    Slot* slots;
    int* control;
    uint8_t* windows;
    int error;
    char* message;
    Bridge* next;
};

std::mutex release_mutex;
Bridge* awaiting_release = nullptr;
int pending_closes = 0;

int& word(Bridge* bridge, int slot) { return bridge->control[slot * 2]; }
int& filled(Bridge* bridge, int slot) { return bridge->control[slot * 2 + 1]; }
int load(int& value) { return __atomic_load_n(&value, __ATOMIC_SEQ_CST); }
uint8_t* data(Bridge* bridge, int slot) { return bridge->windows + (size_t)slot * bridge->window_size; }

int state(Bridge* bridge, int slot, int& current) {
    current = load(word(bridge, slot));
    return ((uint32_t)current >> sequence_shift) == bridge->slots[slot].sequence ? current & state_mask : abandoned;
}

int fail(Bridge* bridge, int error, const char* message) {
    if (bridge->error == 0) {
        bridge->error = error;
        bridge->message = strdup(message);
    }
    return -EIO;
}

void reset_error(Bridge* bridge) {
    free(bridge->message);
    bridge->message = nullptr;
    bridge->error = 0;
}

int wait(Bridge* bridge, int slot, int expected, int milliseconds) {
    int result = lightplayer_completion_wait(&word(bridge, slot), expected, milliseconds);
    if (result == 0 || result == 1 || result == 2) return result;
    char message[128];
    snprintf(message, sizeof(message), "The native completion wait failed (%d).", result);
    fail(bridge, 4, message);
    return result;
}

void retire(Bridge* bridge, int slot) {
    for (;;) {
        int current;
        int value = state(bridge, slot, current);
        if (value == pending) {
            int expected = current;
            if (__atomic_compare_exchange_n(&word(bridge, slot), &expected,
                (current & ~state_mask) | abandoned, false, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST)) return;
        } else if (value == writing) {
            wait(bridge, slot, current, retire_slice);
        } else return;
    }
}

void retire_in_flight(Bridge* bridge) {
    for (int slot = 0; slot < bridge->slot_count; ++slot) retire(bridge, slot);
}

int find(Bridge* bridge, int64_t target) {
    for (int slot = 0; slot < bridge->slot_count; ++slot) {
        Slot& entry = bridge->slots[slot];
        if (target < entry.start) continue;
        int current;
        int value = state(bridge, slot, current);
        int64_t end = entry.start;
        if (value == ready) end += load(filled(bridge, slot));
        else if (value == pending || value == writing) end += entry.requested;
        if (target < end) return slot;
    }
    return -1;
}

int request(Bridge* bridge, int64_t target) {
    int victim = -1;
    bool victim_in_flight = true;
    for (int slot = 0; slot < bridge->slot_count; ++slot) {
        int current;
        int value = state(bridge, slot, current);
        bool in_flight = value == pending || value == writing;
        if (in_flight && !victim_in_flight) continue;
        if (victim < 0 || (victim_in_flight && !in_flight) || bridge->slots[slot].last_use < bridge->slots[victim].last_use) {
            victim = slot;
            victim_in_flight = in_flight;
        }
    }
    retire(bridge, victim);
    Slot& entry = bridge->slots[victim];
    entry.sequence = (entry.sequence + 1) & sequence_mask;
    entry.start = target;
    entry.requested = (int)((bridge->length - target) < bridge->window_size ? bridge->length - target : bridge->window_size);
    entry.last_use = ++bridge->use_clock;
    __atomic_store_n(&filled(bridge, victim), 0, __ATOMIC_SEQ_CST);
    __atomic_store_n(&word(bridge, victim), (int)(entry.sequence << sequence_shift) | pending, __ATOMIC_SEQ_CST);
    MAIN_THREAD_ASYNC_EM_ASM({
        globalThis.lightplayerSourceBridge.request($0, $1, $2, $3, $4, $5, $6);
    }, bridge->source, entry.sequence, (double)target, entry.requested, data(bridge, victim),
        &word(bridge, victim), &filled(bridge, victim));
    return victim;
}

int await_window(Bridge* bridge, int slot) {
    double deadline = emscripten_get_now() + wait_timeout;
    for (;;) {
        int current;
        switch (state(bridge, slot, current)) {
            case ready:
                return load(filled(bridge, slot));
            case failed: {
                int count = load(filled(bridge, slot));
                if (bridge->error == 0) {
                    bridge->error = 1;
                    bridge->message = (char*)malloc((size_t)count + 256);
                    if (bridge->message)
                        snprintf(bridge->message, (size_t)count + 256,
                            "Source %d could not be read at %lld: %.*s", bridge->source,
                            (long long)bridge->slots[slot].start, count, (char*)data(bridge, slot));
                }
                return -EIO;
            }
            case canceled: {
                retire_in_flight(bridge);
                char message[128];
                snprintf(message, sizeof(message), "Reading source %d was canceled.", bridge->source);
                return fail(bridge, 2, message);
            }
            case pending:
            case writing: {
                double remaining = deadline - emscripten_get_now();
                int result = remaining > 0 ? wait(bridge, slot, current, (int)remaining) : 2;
                if (result < 0 || result > 2) return -EIO;
                if (result == 2 && load(word(bridge, slot)) == current && emscripten_get_now() >= deadline) {
                    retire_in_flight(bridge);
                    char message[192];
                    snprintf(message, sizeof(message), "Source %d did not deliver bytes at %lld in time.",
                        bridge->source, (long long)bridge->slots[slot].start);
                    return fail(bridge, 3, message);
                }
                break;
            }
            default:
                return fail(bridge, 4, "A source window was retired while awaited.");
        }
    }
}

int in_flight_count(Bridge* bridge) {
    int count = 0;
    for (int slot = 0; slot < bridge->slot_count; ++slot) {
        int current;
        int value = state(bridge, slot, current);
        if (value == pending || value == writing) ++count;
    }
    return count;
}

void prefetch(Bridge* bridge, int slot) {
    int64_t next = bridge->slots[slot].start + load(filled(bridge, slot));
    for (int ahead = 0; ahead < bridge->prefetch_windows && next < bridge->length; ++ahead) {
        int covering = find(bridge, next);
        if (covering < 0) {
            if (in_flight_count(bridge) >= bridge->prefetch_windows) return;
            covering = request(bridge, next);
        }
        int current;
        next = bridge->slots[covering].start + (state(bridge, covering, current) == ready
            ? load(filled(bridge, covering)) : bridge->slots[covering].requested);
    }
}

void release(Bridge* bridge) {
    free(bridge->slots);
    free(bridge->control);
    free(bridge->windows);
    free(bridge->message);
    free(bridge);
}
}

static int read_source(Bridge* bridge, uint8_t* buffer, int size) {
    if (size <= 0 || bridge->position >= bridge->length) return 0;
    bool sequential = bridge->position == bridge->sequential_end;
    int slot = find(bridge, bridge->position);
    if (slot < 0) slot = request(bridge, bridge->position);
    int count = await_window(bridge, slot);
    if (count < 0) return count;
    int64_t offset = bridge->position - bridge->slots[slot].start;
    if (offset >= count) {
        slot = request(bridge, bridge->position);
        count = await_window(bridge, slot);
        if (count < 0) return count;
        offset = 0;
    }
    if (count <= offset) {
        char message[192];
        snprintf(message, sizeof(message), "Source %d ended at %lld before its stated length of %lld bytes.",
            bridge->source, (long long)bridge->position, (long long)bridge->length);
        return fail(bridge, 1, message);
    }
    count = count - (int)offset < size ? count - (int)offset : size;
    memcpy(buffer, data(bridge, slot) + offset, (size_t)count);
    bridge->position += count;
    bridge->sequential_end = bridge->position;
    bridge->slots[slot].last_use = ++bridge->use_clock;
    if (sequential) prefetch(bridge, slot);
    return count;
}

extern "C" int lightplayer_source_read(Bridge* bridge, uint8_t* buffer, int size) {
    reset_error(bridge);
    return read_source(bridge, buffer, size);
}

extern "C" int64_t lightplayer_source_seek(Bridge* bridge, int64_t offset, int origin) {
    if (origin == av_seek_size) return bridge->length;
    int64_t base;
    switch (origin) {
        case SEEK_SET: base = 0; break;
        case SEEK_CUR: base = bridge->position; break;
        case SEEK_END: base = bridge->length; break;
        default: return -EINVAL;
    }
    if ((offset > 0 && base > INT64_MAX - offset) || (offset < 0 && base < INT64_MIN - offset)) return -EINVAL;
    int64_t target = base + offset;
    if (target < 0) return -EINVAL;
    bridge->position = target;
    return target;
}

namespace {
int read_packet(void* opaque, uint8_t* buffer, int size) {
    int result = read_source((Bridge*)opaque, buffer, size);
    return result == 0 ? av_error_eof : result;
}
int64_t seek_packet(void* opaque, int64_t offset, int origin) {
    return lightplayer_source_seek((Bridge*)opaque, offset, origin);
}
}

struct SourceCallbacks {
    Bridge* opaque;
    int (*read)(void*, uint8_t*, int);
    int64_t (*seek)(void*, int64_t, int);
};

extern "C" int lightplayer_source_create(int source, int64_t length, int window_size, int slot_count,
    int prefetch_windows, SourceCallbacks* callbacks) {
    if (!callbacks || length < 0 || window_size < 4096 || slot_count < 2 || prefetch_windows < 0 || prefetch_windows >= slot_count)
        return -EINVAL;
    *callbacks = {};
    if ((size_t)slot_count > SIZE_MAX / (size_t)window_size || (size_t)slot_count > SIZE_MAX / (2 * sizeof(int))) return -ENOMEM;
    Bridge* bridge = (Bridge*)calloc(1, sizeof(Bridge));
    if (!bridge) return -ENOMEM;
    bridge->slots = (Slot*)calloc((size_t)slot_count, sizeof(Slot));
    bridge->control = (int*)calloc((size_t)slot_count * 2, sizeof(int));
    bridge->windows = (uint8_t*)malloc((size_t)slot_count * window_size);
    if (!bridge->slots || !bridge->control || !bridge->windows) {
        release(bridge);
        return -ENOMEM;
    }
    bridge->source = source;
    bridge->length = length;
    bridge->window_size = window_size;
    bridge->slot_count = slot_count;
    bridge->prefetch_windows = prefetch_windows;
    bridge->sequential_end = -1;
    *callbacks = { bridge, read_packet, seek_packet };
    return 0;
}

extern "C" void lightplayer_source_reset_error(Bridge* bridge) {
    reset_error(bridge);
}

extern "C" int lightplayer_source_error(Bridge* bridge, const char** message) {
    *message = bridge->message;
    return bridge->error;
}

extern "C" void lightplayer_source_dispose(Bridge* bridge) {
    retire_in_flight(bridge);
    {
        std::lock_guard<std::mutex> guard(release_mutex);
        bridge->next = awaiting_release;
        awaiting_release = bridge;
        ++pending_closes;
    }
    MAIN_THREAD_ASYNC_EM_ASM({ globalThis.lightplayerSourceBridge.close($0); }, bridge->source);
}

extern "C" void lightplayer_source_release_closed(int source) {
    std::lock_guard<std::mutex> guard(release_mutex);
    Bridge** link = &awaiting_release;
    while (*link) {
        Bridge* bridge = *link;
        if (bridge->source == source) {
            *link = bridge->next;
            --pending_closes;
            release(bridge);
            return;
        }
        link = &bridge->next;
    }
}

extern "C" int lightplayer_source_pending_closes() {
    std::lock_guard<std::mutex> guard(release_mutex);
    return pending_closes;
}