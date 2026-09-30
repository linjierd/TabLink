#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

int av_usleep(unsigned usec);

static int compare_double(const void *left, const void *right)
{
    double a = *(const double *)left, b = *(const double *)right;
    return (a > b) - (a < b);
}

static void sample(unsigned usec, int high_resolution)
{
    LARGE_INTEGER frequency, before, after;
    double gaps[100], sum = 0;
    int i;
    QueryPerformanceFrequency(&frequency);
    for (i = 0; i < 100; i++) {
        QueryPerformanceCounter(&before);
        if (high_resolution)
            av_usleep(usec);
        else
            Sleep(usec / 1000);
        QueryPerformanceCounter(&after);
        gaps[i] = (double)(after.QuadPart - before.QuadPart) * 1000 / frequency.QuadPart;
        sum += gaps[i];
    }
    qsort(gaps, 100, sizeof(gaps[0]), compare_double);
    printf("%s,%u,%.6f,%.6f,%.6f,%.6f,%.6f\n",
           high_resolution ? "patched_av_usleep" : "original_Sleep",
           usec, gaps[0], gaps[50], gaps[90], gaps[99], sum / 100);
}

int main(void)
{
    DWORD before, after;
    int i;
    puts("method,requested_us,min_ms,median_ms,p90_ms,max_ms,mean_ms");
    sample(1000, 0); sample(5000, 0); sample(11111, 0);
    sample(1000, 1); sample(5000, 1); sample(11111, 1);
    GetProcessHandleCount(GetCurrentProcess(), &before);
    for (i = 0; i < 1000; i++) av_usleep(1);
    for (i = 0; i < 1000; i++) av_usleep(0);
    GetProcessHandleCount(GetCurrentProcess(), &after);
    fprintf(stderr, "HandleCountBefore=%lu HandleCountAfter=%lu\n", before, after);
    return after == before ? 0 : 1;
}
