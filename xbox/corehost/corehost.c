// Desktop test host for Ryujinx.Xbox.Core: loads the NativeAOT DLL the same way the Xbox shell will,
// runs one program and prints memory and guest progress. Used by CI before anything reaches the console.
//
// usage: corehost <core.dll> <base dir> <program.nro> <seconds> [memory manager mode 0|1|2]
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

typedef struct
{
    int64_t private_bytes;
    int64_t working_set;
    int64_t peak_working_set;
    int64_t managed_heap;
    int64_t uptime_ms;
    int32_t running;
    int32_t guest_markers;
} xbc_stats;

typedef int (*xbc_init_fn)(const char *base_dir);
typedef int (*xbc_load_fn)(const char *path, int memory_manager_mode);
typedef void (*xbc_get_stats_fn)(xbc_stats *stats);
typedef int (*xbc_read_log_fn)(char *buffer, int capacity);
typedef void (*xbc_shutdown_fn)(void);

static void print_stats(const char *label, xbc_stats *s)
{
    printf("[%s] t=%lldms private=%lldMB ws=%lldMB peak=%lldMB managed=%lldMB markers=%d\n", label,
           (long long)s->uptime_ms, (long long)(s->private_bytes >> 20), (long long)(s->working_set >> 20),
           (long long)(s->peak_working_set >> 20), (long long)(s->managed_heap >> 20), s->guest_markers);
    fflush(stdout);
}

int main(int argc, char **argv)
{
    if (argc < 5)
    {
        fprintf(stderr, "usage: corehost <core.dll> <base dir> <program.nro> <seconds> [mode]\n");
        return 2;
    }

    int mode = argc > 5 ? atoi(argv[5]) : 2;
    HMODULE core = LoadLibraryA(argv[1]);
    if (!core)
    {
        fprintf(stderr, "LoadLibrary failed: %lu\n", GetLastError());
        return 3;
    }

    xbc_init_fn init = (xbc_init_fn)GetProcAddress(core, "xbc_init");
    xbc_load_fn load = (xbc_load_fn)GetProcAddress(core, "xbc_load");
    xbc_get_stats_fn get_stats = (xbc_get_stats_fn)GetProcAddress(core, "xbc_get_stats");
    xbc_read_log_fn read_log = (xbc_read_log_fn)GetProcAddress(core, "xbc_read_log");
    xbc_shutdown_fn shutdown = (xbc_shutdown_fn)GetProcAddress(core, "xbc_shutdown");
    if (!init || !load || !get_stats || !read_log || !shutdown)
    {
        fprintf(stderr, "missing exports\n");
        return 4;
    }

    xbc_stats stats;
    int rc = init(argv[2]);
    get_stats(&stats);
    print_stats("init", &stats);
    if (rc != 0)
        goto dump;

    rc = load(argv[3], mode);
    printf("load rc=%d mode=%d\n", rc, mode);
    if (rc != 0)
        goto dump;

    int seconds = atoi(argv[4]);
    for (int i = 0; i < seconds; i++)
    {
        Sleep(1000);
        get_stats(&stats);
        print_stats("run", &stats);
    }

dump:;
    static char log[1 << 20];
    int n = read_log(log, sizeof(log) - 1);
    log[n] = 0;
    printf("----- core log (tail) -----\n%s\n", log);
    shutdown();
    return rc != 0 ? 1 : 0;
}
