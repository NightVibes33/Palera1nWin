#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <setupapi.h>
#include <initguid.h>
#include <usbiodef.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

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

static wchar_t *build_command_line(int argc, wchar_t **argv, const wchar_t *core)
{
    size_t capacity = wcslen(core) + 4;
    for (int index = 1; index < argc; ++index) capacity += wcslen(argv[index]) * 2 + 4;

    wchar_t *command = calloc(capacity, sizeof(wchar_t));
    if (command == NULL) return NULL;
    swprintf(command, capacity, L"\"%ls\"", core);
    for (int index = 1; index < argc; ++index) {
        wcscat(command, L" \"");
        wcscat(command, argv[index]);
        wcscat(command, L"\"");
    }
    return command;
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

    wchar_t *command = build_command_line(argc, argv, core);
    if (command == NULL) {
        fwprintf(stderr, L"[DarkSword] Out of memory.\n");
        return 92;
    }

    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    ZeroMemory(&startup, sizeof(startup));
    ZeroMemory(&process, sizeof(process));
    startup.cb = sizeof(startup);

    fwprintf(stdout, L"[DarkSword] Starting Windows-native openra1n checkm8/PongoOS core. DFU remains owned by Windows until 05AC:4141 appears.\n");
    fflush(stdout);

    if (!CreateProcessW(core, command, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &process)) {
        DWORD error = GetLastError();
        free(command);
        fwprintf(stderr, L"[DarkSword] Could not start openra1n-core.exe (error=%lu).\n", error);
        return 93;
    }
    free(command);

    const DWORD total_timeout_ms = 120000;
    const DWORD post_exit_grace_ms = 30000;
    DWORD elapsed = 0;
    DWORD child_exit_elapsed = 0;
    DWORD child_exit_code = STILL_ACTIVE;
    DWORD returned_mode_samples = 0;
    int child_exited = 0;
    int pongo_seen = 0;
    int returned_to_apple_mode = 0;

    while (elapsed < total_timeout_ms) {
        apple_usb_mode_t mode = detect_apple_usb_mode();
        if (mode == APPLE_USB_PONGO) {
            pongo_seen = 1;
            fwprintf(stdout, L"[DarkSword] PongoOS USB 05AC:4141 enumerated. Returning control to the managed Windows-to-WSL handoff.\n");
            fflush(stdout);
            break;
        }

        if (!child_exited && WaitForSingleObject(process.hProcess, 0) == WAIT_OBJECT_0) {
            child_exited = 1;
            GetExitCodeProcess(process.hProcess, &child_exit_code);
            child_exit_elapsed = 0;
            fwprintf(stdout,
                L"[DarkSword] openra1n-core.exe exited with code %lu after the USB payload stage; allowing %lu ms for PongoOS re-enumeration.\n",
                child_exit_code,
                post_exit_grace_ms);
            fflush(stdout);
        }

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
    if (pongo_seen) {
        Sleep(500);
        if (WaitForSingleObject(process.hProcess, 0) == WAIT_TIMEOUT) {
            TerminateProcess(process.hProcess, 0);
            WaitForSingleObject(process.hProcess, 3000);
        }
        exit_code = 0;
    } else if (returned_to_apple_mode) {
        fwprintf(stderr,
            L"[DarkSword] The checkm8 transfer completed, but the device returned to normal/recovery Apple USB mode instead of PongoOS. The embedded openra1n payload did not remain running.\n");
        exit_code = 96;
    } else if (child_exited) {
        fwprintf(stderr,
            L"[DarkSword] openra1n-core.exe exited (code=%lu), and PongoOS did not enumerate during the %lu ms grace period.\n",
            child_exit_code,
            post_exit_grace_ms);
        exit_code = child_exit_code == 0 ? 95 : child_exit_code;
    } else {
        fwprintf(stderr, L"[DarkSword] Timed out waiting for PongoOS USB 05AC:4141.\n");
        TerminateProcess(process.hProcess, 94);
        WaitForSingleObject(process.hProcess, 3000);
        exit_code = 94;
    }

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return (int)exit_code;
}
