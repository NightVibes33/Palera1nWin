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
    wchar_t installer[MAX_PATH];
    if (!sibling_path(L"openra1n-core.exe", core, MAX_PATH) ||
        !sibling_path(L"wdi-simple.exe", installer, MAX_PATH)) {
        fwprintf(stderr, L"[DarkSword] Could not resolve bundled toolchain paths.\n");
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

    fwprintf(stdout, L"[DarkSword] Starting Windows checkm8/PongoOS stage.\n");
    intptr_t core_exit = _wspawnv(_P_WAIT, core, (const wchar_t * const *)core_argv);
    free(core_argv);
    if (core_exit == -1) {
        fwprintf(stderr, L"[DarkSword] Could not start openra1n-core.exe (errno=%d).\n", errno);
        return 93;
    }
    if (core_exit != 0) {
        fwprintf(stderr, L"[DarkSword] openra1n-core.exe exited with code %lld.\n", (long long)core_exit);
        return (int)core_exit;
    }

    if (GetFileAttributesW(installer) == INVALID_FILE_ATTRIBUTES) {
        fwprintf(stderr, L"[DarkSword] Missing %ls\n", installer);
        return 94;
    }

    const wchar_t *driver_argv[] = {
        installer,
        L"--vid", L"0x05AC",
        L"--pid", L"0x4141",
        L"--type", L"2",
        L"--name", L"Apple Mobile Device (PongoOS Mode)",
        NULL
    };

    fwprintf(stdout, L"[DarkSword] Assigning libusbK to PongoOS USB 05AC:4141.\n");
    intptr_t driver_exit = -1;
    for (int attempt = 1; attempt <= 10; ++attempt) {
        driver_exit = _wspawnv(_P_WAIT, installer, driver_argv);
        if (driver_exit == 0) {
            break;
        }
        fwprintf(stderr,
                 L"[DarkSword] PongoOS driver attempt %d returned %lld; retrying.\n",
                 attempt,
                 (long long)driver_exit);
        Sleep(500);
    }
    if (driver_exit != 0) {
        fwprintf(stderr,
                 L"[DarkSword] PongoOS libusbK installation failed with code %lld.\n",
                 (long long)driver_exit);
        return 95;
    }

    Sleep(2000);
    fwprintf(stdout, L"[DarkSword] PongoOS driver is ready.\n");
    return 0;
}
