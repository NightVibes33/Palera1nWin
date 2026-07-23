#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <setupapi.h>
#include <initguid.h>
#include <usbiodef.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <wctype.h>

typedef enum {
    APPLE_USB_NONE = 0,
    APPLE_USB_DFU = 1,
    APPLE_USB_PONGO = 2,
    APPLE_USB_OTHER = 3
} apple_usb_mode_t;

static int sibling_path(const wchar_t *name, wchar_t *output, size_t capacity)
{
    DWORD length = GetModuleFileNameW(NULL, output, (DWORD)capacity);
    if (length == 0 || length >= capacity) return 0;

    wchar_t *slash = wcsrchr(output, L'\\');
    if (slash == NULL) slash = wcsrchr(output, L'/');
    if (slash == NULL) return 0;

    slash[1] = L'\0';
    if (wcslen(output) + wcslen(name) + 1 > capacity) return 0;
    wcscat(output, name);
    return 1;
}

static int powershell_path(wchar_t *output, size_t capacity)
{
    wchar_t windows_directory[MAX_PATH];
    DWORD length = GetWindowsDirectoryW(windows_directory, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return 0;

    int written = swprintf(
        output,
        capacity,
        L"%ls\\System32\\WindowsPowerShell\\v1.0\\powershell.exe",
        windows_directory);
    return written > 0 && (size_t)written < capacity;
}

static int contains_case_insensitive(const wchar_t *text, const wchar_t *needle)
{
    size_t text_length = wcslen(text);
    size_t needle_length = wcslen(needle);
    if (needle_length == 0 || needle_length > text_length) return 0;

    for (size_t offset = 0; offset + needle_length <= text_length; ++offset) {
        if (_wcsnicmp(text + offset, needle, needle_length) == 0) return 1;
    }
    return 0;
}

static apple_usb_mode_t detect_apple_usb_mode(void)
{
    HDEVINFO info = SetupDiGetClassDevsW(
        &GUID_DEVINTERFACE_USB_DEVICE,
        NULL,
        NULL,
        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (info == INVALID_HANDLE_VALUE) return APPLE_USB_NONE;

    apple_usb_mode_t best = APPLE_USB_NONE;
    for (DWORD index = 0; ; ++index) {
        SP_DEVICE_INTERFACE_DATA interface_data;
        ZeroMemory(&interface_data, sizeof(interface_data));
        interface_data.cbSize = sizeof(interface_data);
        if (!SetupDiEnumDeviceInterfaces(info, NULL, &GUID_DEVINTERFACE_USB_DEVICE, index, &interface_data)) {
            break;
        }

        DWORD required = 0;
        SetupDiGetDeviceInterfaceDetailW(info, &interface_data, NULL, 0, &required, NULL);
        if (required == 0) continue;

        PSP_DEVICE_INTERFACE_DETAIL_DATA_W detail = malloc(required);
        if (detail == NULL) break;
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
        if (SetupDiGetDeviceInterfaceDetailW(info, &interface_data, detail, required, NULL, NULL) &&
            contains_case_insensitive(detail->DevicePath, L"vid_05ac")) {
            if (contains_case_insensitive(detail->DevicePath, L"pid_4141")) {
                best = APPLE_USB_PONGO;
                free(detail);
                break;
            }
            if (contains_case_insensitive(detail->DevicePath, L"pid_1227")) {
                if (best == APPLE_USB_NONE) best = APPLE_USB_DFU;
            } else {
                best = APPLE_USB_OTHER;
            }
        }
        free(detail);
    }

    SetupDiDestroyDeviceInfoList(info);
    return best;
}

static int valid_distro_name(const wchar_t *value)
{
    if (value == NULL || *value == L'\0') return 0;
    for (const wchar_t *cursor = value; *cursor != L'\0'; ++cursor) {
        if (!(iswalnum(*cursor) || *cursor == L'.' || *cursor == L'_' || *cursor == L'-')) {
            return 0;
        }
    }
    return 1;
}

static int resolve_distro(wchar_t *output, size_t capacity)
{
    DWORD length = GetEnvironmentVariableW(L"PALERA1NWIN_DISTRO", output, (DWORD)capacity);
    if (length == 0) {
        if (capacity < 8) return 0;
        wcscpy(output, L"Ubuntu");
        return 1;
    }
    if (length >= capacity || !valid_distro_name(output)) return 0;
    return 1;
}

static wchar_t *build_command_line(
    const wchar_t *powershell,
    const wchar_t *script,
    const wchar_t *distro)
{
    const wchar_t *format =
        L"\"%ls\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass "
        L"-File \"%ls\" -- -p -S --yes --distro \"%ls\"";
    size_t capacity = wcslen(format) + wcslen(powershell) + wcslen(script) + wcslen(distro) + 32;
    wchar_t *command = calloc(capacity, sizeof(wchar_t));
    if (command == NULL) return NULL;
    if (swprintf(command, capacity, format, powershell, script, distro) < 0) {
        free(command);
        return NULL;
    }
    return command;
}

int wmain(void)
{
    wchar_t script[MAX_PATH];
    wchar_t legacy_core[MAX_PATH];
    wchar_t powershell[MAX_PATH];
    wchar_t distro[128];

    if (!sibling_path(L"windows\\palera1n.ps1", script, MAX_PATH) ||
        GetFileAttributesW(script) == INVALID_FILE_ATTRIBUTES) {
        fwprintf(stderr, L"[DarkSword] Missing packaged windows\\palera1n.ps1 launcher.\n");
        return 90;
    }
    if (!powershell_path(powershell, MAX_PATH) ||
        GetFileAttributesW(powershell) == INVALID_FILE_ATTRIBUTES) {
        fwprintf(stderr, L"[DarkSword] Windows PowerShell 5.1 was not found.\n");
        return 91;
    }
    if (!resolve_distro(distro, sizeof(distro) / sizeof(distro[0]))) {
        fwprintf(stderr, L"[DarkSword] PALERA1NWIN_DISTRO contains an invalid WSL distribution name.\n");
        return 92;
    }

    wchar_t *command = build_command_line(powershell, script, distro);
    if (command == NULL) {
        fwprintf(stderr, L"[DarkSword] Could not build the official Pongo loader command line.\n");
        return 93;
    }

    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);

    fwprintf(stdout,
        L"[DarkSword] Starting official palera1n matched checkra1n/PongoOS loader in WSL '%ls'. USB drivers remain managed by the host application.\n",
        distro);
    if (sibling_path(L"openra1n-core.exe", legacy_core, MAX_PATH) &&
        GetFileAttributesW(legacy_core) != INVALID_FILE_ATTRIBUTES) {
        fwprintf(stdout,
            L"[DarkSword] openra1n-core.exe remains packaged only for diagnostics; the production Pongo boot no longer mixes its legacy shellcode with a different Pongo image.\n");
    }
    fflush(stdout);

    if (!CreateProcessW(powershell, command, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &process)) {
        DWORD error = GetLastError();
        free(command);
        fwprintf(stderr, L"[DarkSword] Could not start the official palera1n Pongo loader (error=%lu).\n", error);
        return 94;
    }
    free(command);

    const DWORD total_timeout_ms = 240000;
    const DWORD post_exit_grace_ms = 60000;
    DWORD elapsed = 0;
    DWORD child_exit_elapsed = 0;
    DWORD child_exit_code = STILL_ACTIVE;
    DWORD returned_mode_samples = 0;
    int child_exited = 0;
    int pongo_seen = 0;
    int returned_to_apple_mode = 0;

    while (elapsed < total_timeout_ms) {
        apple_usb_mode_t mode = detect_apple_usb_mode();
        if (mode == APPLE_USB_PONGO && !pongo_seen) {
            pongo_seen = 1;
            fwprintf(stdout,
                L"[DarkSword] PongoOS USB 05AC:4141 enumerated from the official matched loader. Returning control to the managed driver/probe pipeline.\n");
            fflush(stdout);
        }

        if (!child_exited && WaitForSingleObject(process.hProcess, 0) == WAIT_OBJECT_0) {
            child_exited = 1;
            GetExitCodeProcess(process.hProcess, &child_exit_code);
            child_exit_elapsed = 0;
            fwprintf(stdout,
                L"[DarkSword] Official palera1n Pongo loader exited with code %lu; allowing %lu ms for Windows USB re-enumeration.\n",
                child_exit_code,
                post_exit_grace_ms);
            fflush(stdout);
        }

        if (child_exited && pongo_seen) break;

        if (child_exited && mode == APPLE_USB_OTHER) {
            returned_mode_samples++;
            if (returned_mode_samples >= 4) {
                returned_to_apple_mode = 1;
                break;
            }
        } else if (mode != APPLE_USB_OTHER) {
            returned_mode_samples = 0;
        }

        if (child_exited && child_exit_elapsed >= post_exit_grace_ms) break;
        Sleep(250);
        elapsed += 250;
        if (child_exited) child_exit_elapsed += 250;
    }

    DWORD exit_code = 1;
    if (pongo_seen && child_exit_code == 0) {
        exit_code = 0;
    } else if (pongo_seen && child_exited) {
        fwprintf(stderr,
            L"[DarkSword] PongoOS enumerated, but the official loader exited with code %lu.\n",
            child_exit_code);
        exit_code = child_exit_code;
    } else if (returned_to_apple_mode) {
        fwprintf(stderr,
            L"[DarkSword] Official palera1n completed the DFU attempt, but the device returned to normal/recovery mode instead of PongoOS.\n");
        exit_code = 96;
    } else if (child_exited) {
        fwprintf(stderr,
            L"[DarkSword] Official palera1n loader exited (code=%lu), and PongoOS USB 05AC:4141 did not enumerate during the %lu ms grace period.\n",
            child_exit_code,
            post_exit_grace_ms);
        exit_code = child_exit_code == 0 ? 95 : child_exit_code;
    } else {
        fwprintf(stderr, L"[DarkSword] Timed out waiting for the official palera1n PongoOS handoff.\n");
        TerminateProcess(process.hProcess, 97);
        WaitForSingleObject(process.hProcess, 3000);
        exit_code = 97;
    }

    if (!child_exited && WaitForSingleObject(process.hProcess, 0) == WAIT_TIMEOUT) {
        TerminateProcess(process.hProcess, exit_code);
        WaitForSingleObject(process.hProcess, 3000);
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return (int)exit_code;
}
