#include <pthread.h>
#include <stdint.h>

// Review after upgrading the .NET runtime: https://github.com/dotnet/runtime/issues/129900
extern "C" uint32_t __wrap_SystemNative_TryGetUInt32OSThreadId(void) {
    return (uint32_t)(uintptr_t)pthread_self();
}
