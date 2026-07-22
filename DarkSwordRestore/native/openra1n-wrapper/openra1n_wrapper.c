#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <process.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

static int sibling_path(const wchar_t *name, wchar_t *output, size_t capacity)
{
    DWORD length = GetModuleFileNameW(NULL, output, (DWORD)capacity);
    if (length == 0 || length >= capacity) {
        return 0;
    }

    wchar_t *slash = wcsrchr(output, L'\\');
    if (slash == NULL) {
        slash = wcsrchr(output, L'/');
    }
    if (slash == NULL) {
        return 0;
    }

    slash[1] = L'\0';
    size_t used = wcslen(output);
    size_t needed = wcslen(name);
    if (used + needed + 1 > capacity) {
        return 0;
    }
    wcscat(output, name);
    return 1;
}

int wmain(int argc, wchar_t **argv)
{
    wchar_t core[MAX_PATH];
    if (!sibling_path(L"openra1n-core.exe", core, MAX_PATH)) {
        fwprintf(stderr, L"[DarkSword] Could not resolve openra1n-core.exe.\n");
        return 90;
    }

    if (GetFileAttributesW(core) == INVALID_FILE_ATTRIBUTES) {
        fwprintf(stderr, L"[DarkSword] Missing %ls\n", core);
        return 91;
    }

    wchar_t **core_argv = calloc((size_t)argc + 1, sizeof(wchar_t *));
    if (core_argv == NULL) {
        fwprintf(stderr, L"[DarkSword] Out of memory.\n");
        return 92;
    }

    core_argv[0] = core;
    for (int index = 1; index < argc; ++index) {
        core_argv[index] = argv[index];
    }
    core_argv[argc] = NULL;

    fwprintf(stdout, L"[DarkSword] Starting Windows checkm8/PongoOS core. Driver ownership remains in the managed hardware pipeline.\n");
    fflush(stdout);

    /* Replace this wrapper process with the real core process. The managed host
       now owns process lifetime, USB driver transitions, Pongo enumeration,
       and bridge verification. No wdi-simple call is allowed in this wrapper. */
    intptr_t result = _wspawnv(_P_OVERLAY, core, (const wchar_t * const *)core_argv);
    free(core_argv);

    fwprintf(stderr, L"[DarkSword] Could not start openra1n-core.exe (errno=%d, result=%lld).\n",
             errno,
             (long long)result);
    return 93;
}
