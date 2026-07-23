#include <libusb-1.0/libusb.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#define sleep_ms(value) Sleep((DWORD)(value))
#else
#include <unistd.h>
#define sleep_ms(value) usleep((value) * 1000)
#endif

#define APPLE_VID 0x05AC
#define PONGO_PID 0x4141
#define PONGO_INTERFACE 0
#define PONGO_BULK_OUT 0x02
#define CONTROL_TIMEOUT 5000
#define BULK_TIMEOUT 10000
#define PONGO_CMD_BULK 1
#define PONGO_CMD_STATUS 2
#define PONGO_CMD_SEND 3
#define PONGO_CMD_CLEAR 4

static void die_libusb(const char *operation, int code) {
    fprintf(stderr, "[error] %s: %s (%d)\n", operation, libusb_error_name(code), code);
    exit(2);
}

static unsigned char *read_file(const char *path, size_t *length) {
    FILE *file = fopen(path, "rb");
    if (!file) {
        fprintf(stderr, "[error] unable to open %s\n", path);
        exit(3);
    }
    if (fseek(file, 0, SEEK_END) != 0) {
        fclose(file);
        fprintf(stderr, "[error] unable to seek resource: %s\n", path);
        exit(3);
    }
    long size = ftell(file);
    if (size <= 0 || fseek(file, 0, SEEK_SET) != 0) {
        fclose(file);
        fprintf(stderr, "[error] invalid resource file: %s\n", path);
        exit(3);
    }
    unsigned char *buffer = (unsigned char *)malloc((size_t)size);
    if (!buffer) {
        fclose(file);
        fprintf(stderr, "[error] out of memory reading %s\n", path);
        exit(3);
    }
    size_t read = fread(buffer, 1, (size_t)size, file);
    fclose(file);
    if (read != (size_t)size) {
        free(buffer);
        fprintf(stderr, "[error] short read from %s\n", path);
        exit(3);
    }
    *length = read;
    return buffer;
}

static int control_out(libusb_device_handle *device, uint8_t request, uint16_t value, unsigned char *data, uint16_t length) {
    return libusb_control_transfer(device, 0x21, request, value, 0, data, length, CONTROL_TIMEOUT);
}

static int control_in(libusb_device_handle *device, uint8_t request, unsigned char *data, uint16_t length) {
    return libusb_control_transfer(device, 0xA1, request, 0, 0, data, length, CONTROL_TIMEOUT);
}

static void pongo_clear(libusb_device_handle *device) {
    int result = control_out(device, PONGO_CMD_CLEAR, 0xFFFF, NULL, 0);
    if (result < 0) die_libusb("Pongo clear", result);
    if (result != 0) {
        fprintf(stderr, "[error] unexpected Pongo clear response length: %d\n", result);
        exit(4);
    }
}

static void pongo_wait_done(libusb_device_handle *device, unsigned timeout_ms) {
    unsigned elapsed = 0;
    while (elapsed < timeout_ms) {
        unsigned char in_progress = 1;
        int result = control_in(device, PONGO_CMD_STATUS, &in_progress, 1);
        if (result == 1) {
            if (in_progress == 0) return;
            if (in_progress != 1) {
                fprintf(stderr, "[error] invalid Pongo status byte: %u\n", (unsigned)in_progress);
                exit(4);
            }
        } else if (result >= 0) {
            fprintf(stderr, "[error] short Pongo status response: %d/1\n", result);
            exit(4);
        } else if (result != LIBUSB_ERROR_PIPE && result != LIBUSB_ERROR_TIMEOUT) {
            die_libusb("Pongo status", result);
        }
        sleep_ms(10);
        elapsed += 10;
    }
    fprintf(stderr, "[error] Pongo command timed out\n");
    exit(4);
}

static void pongo_send_command(libusb_device_handle *device, const char *command) {
    pongo_clear(device);
    size_t length = strlen(command);
    if (length == 0 || length > UINT16_MAX) {
        fprintf(stderr, "[error] invalid Pongo command length: %zu\n", length);
        exit(4);
    }
    printf("[cmd] %s", command);
    fflush(stdout);
    int result = control_out(device, PONGO_CMD_SEND, 0, (unsigned char *)command, (uint16_t)length);
    if (result < 0) die_libusb("Pongo command", result);
    if ((size_t)result != length) {
        fprintf(stderr, "[error] short Pongo command transfer (%d/%zu)\n", result, length);
        exit(4);
    }
    pongo_wait_done(device, 15000);
}

static void pongo_send_resource(libusb_device_handle *device, const char *name, const char *path) {
    size_t length = 0;
    unsigned char *data = read_file(path, &length);
    if (length > UINT32_MAX) {
        free(data);
        fprintf(stderr, "[error] resource is too large: %s\n", path);
        exit(3);
    }

    printf("[upload] %s (%zu bytes)\n", name, length);
    pongo_clear(device);
    uint32_t size32 = (uint32_t)length;
    int result = control_out(device, PONGO_CMD_BULK, 0, (unsigned char *)&size32, sizeof(size32));
    if (result != (int)sizeof(size32)) {
        free(data);
        if (result < 0) die_libusb("Pongo bulk setup", result);
        fprintf(stderr, "[error] short Pongo bulk setup\n");
        exit(4);
    }

    size_t offset = 0;
    int last_percent = -1;
    while (offset < length) {
        int transferred = 0;
        int chunk = (int)((length - offset) > 2048 ? 2048 : (length - offset));
        result = libusb_bulk_transfer(device, PONGO_BULK_OUT, data + offset, chunk, &transferred, BULK_TIMEOUT);
        if (result < 0) {
            free(data);
            die_libusb("Pongo bulk data", result);
        }
        if (transferred <= 0 || transferred > chunk) {
            free(data);
            fprintf(stderr, "[error] invalid Pongo bulk progress (%d/%d)\n", transferred, chunk);
            exit(4);
        }
        offset += (size_t)transferred;
        int percent = (int)((offset * 100U) / length);
        if (percent != last_percent && (percent == 100 || percent % 5 == 0)) {
            printf("[progress] %s %d%%\n", name, percent);
            fflush(stdout);
            last_percent = percent;
        }
    }
    free(data);
}

static libusb_device_handle *open_single_pongo(libusb_context **context) {
    int result = libusb_init_context(context, NULL, 0);
    if (result < 0) die_libusb("libusb initialization", result);

    libusb_device **list = NULL;
    ssize_t count = libusb_get_device_list(*context, &list);
    if (count < 0) {
        libusb_exit(*context);
        die_libusb("enumerating USB devices", (int)count);
    }

    libusb_device *selected = NULL;
    int matches = 0;
    for (ssize_t index = 0; index < count; ++index) {
        struct libusb_device_descriptor descriptor;
        if (libusb_get_device_descriptor(list[index], &descriptor) != 0) continue;
        if (descriptor.idVendor == APPLE_VID && descriptor.idProduct == PONGO_PID) {
            selected = list[index];
            matches++;
        }
    }

    if (matches != 1 || selected == NULL) {
        fprintf(stderr, matches == 0
            ? "[error] PongoOS device 05AC:4141 was not found\n"
            : "[error] multiple PongoOS devices were found; disconnect every non-target Apple device\n");
        libusb_free_device_list(list, 1);
        libusb_exit(*context);
        exit(2);
    }

    libusb_device_handle *device = NULL;
    result = libusb_open(selected, &device);
    libusb_free_device_list(list, 1);
    if (result < 0 || !device) {
        libusb_exit(*context);
        die_libusb("opening the single Pongo device", result < 0 ? result : LIBUSB_ERROR_OTHER);
    }

    libusb_set_auto_detach_kernel_driver(device, 1);
    result = libusb_claim_interface(device, PONGO_INTERFACE);
    if (result < 0 && result != LIBUSB_ERROR_NOT_SUPPORTED) {
        libusb_close(device);
        libusb_exit(*context);
        die_libusb("claiming Pongo interface", result);
    }
    return device;
}

static const char *argument_value(int argc, char **argv, const char *name) {
    for (int index = 2; index + 1 < argc; ++index) {
        if (strcmp(argv[index], name) == 0) return argv[index + 1];
    }
    return NULL;
}

static void usage(const char *program) {
    fprintf(stderr,
        "DarkSword Pongo Bridge\n"
        "Usage:\n"
        "  %s probe\n"
        "  %s boot --pteblock FILE --sep-racer FILE --kpf FILE\n",
        program, program);
}

int main(int argc, char **argv) {
    if (argc < 2) {
        usage(argv[0]);
        return 1;
    }

    libusb_context *context = NULL;
    libusb_device_handle *device = open_single_pongo(&context);

    if (strcmp(argv[1], "probe") == 0) {
        printf("Exactly one PongoOS device detected (05AC:4141)\n");
    } else if (strcmp(argv[1], "boot") == 0) {
        const char *pteblock = argument_value(argc, argv, "--pteblock");
        const char *sep_racer = argument_value(argc, argv, "--sep-racer");
        const char *kpf = argument_value(argc, argv, "--kpf");
        if (!pteblock || !sep_racer || !kpf) {
            usage(argv[0]);
            libusb_release_interface(device, PONGO_INTERFACE);
            libusb_close(device);
            libusb_exit(context);
            return 1;
        }

        sleep_ms(500);
        pongo_send_resource(device, "sep_racer", sep_racer);
        pongo_send_command(device, "modload\n");
        pongo_send_resource(device, "pteblock", pteblock);
        pongo_send_command(device, "sep pte\n");
        pongo_send_command(device, "sep pwn_pte\n");
        sleep_ms(500);
        pongo_send_resource(device, "kpf_tethered", kpf);
        pongo_send_command(device, "modload\n");
        pongo_send_command(device, "kpf-tethered\n");
        pongo_send_command(device, "bootux\n");
        printf("[complete] tether boot commands acknowledged by the Pongo status protocol\n");
    } else {
        usage(argv[0]);
        libusb_release_interface(device, PONGO_INTERFACE);
        libusb_close(device);
        libusb_exit(context);
        return 1;
    }

    libusb_release_interface(device, PONGO_INTERFACE);
    libusb_close(device);
    libusb_exit(context);
    return 0;
}
