# Security boundaries

- DarkSword Restore operates only on a physically connected Apple device.
- It does not implement Activation Lock bypass or credential collection.
- Apple ID credentials are never requested or stored by the application.
- The DFU driver installer targets only USB VID `05AC`, PID `1227`.
- The application never disables Windows Driver Signature Enforcement.
- Firmware archives are read-only inputs and are not rewritten.
- Session logs remain local and should be reviewed before sharing because native tools may print device identifiers.
- Release packages include file hashes so users can verify their download.
