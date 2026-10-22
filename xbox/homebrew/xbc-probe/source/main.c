// Stage-0 probe for the Xbox core. No GPU, no console: it only talks through svcOutputDebugString,
// which Ryujinx logs, so the host can follow progress by counting "XBC_" markers.
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <switch.h>

static void mark(const char *fmt, ...)
{
    char line[128];
    va_list args;
    va_start(args, fmt);
    int len = vsnprintf(line, sizeof(line), fmt, args);
    va_end(args);
    svcOutputDebugString(line, len);
}

int main(int argc, char **argv)
{
    mark("XBC_JIT_ALIVE");

    // Touch guest memory in 64 MiB steps so the host can watch its own memory grow.
    const size_t step = 64u << 20;
    const int steps = 24; // 1.5 GiB
    void *blocks[24] = {0};
    for (int i = 0; i < steps; i++)
    {
        blocks[i] = malloc(step);
        if (!blocks[i])
        {
            mark("XBC_MEM_FAIL %d", i);
            break;
        }
        memset(blocks[i], i + 1, step);
        mark("XBC_MEM %d MiB", (i + 1) * 64);
    }

    // Some CPU work so the JIT has real code to translate.
    u64 acc = 0;
    for (u64 i = 0; i < 200000000ull; i++)
        acc = acc * 6364136223846793005ull + i;
    mark("XBC_CPU_DONE %llu", (unsigned long long)(acc & 0xffff));

    for (int i = 0; i < steps; i++)
        free(blocks[i]);

    mark("XBC_DONE");
    return 0;
}
