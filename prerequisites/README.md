# Prerequisites and Drivers Directory

This directory contains external installers and driver packages required by RhythmHub.

## Included Prerequisites

1. **Nefarius VirtualPad Runtime (ViGEmBus)**:
   - File: `ViGEmBus_1.22.0_x64_x86_arm64.exe`
   - Purpose: Emulates virtual Xbox 360 and DualShock 4 gamepads in Windows, allowing RhythmHub to map guitar and rhythm game controllers as standard Windows gamepads.
   - Installation: Automatically checked and offered during the RhythmHub setup wizard.

## Optional External Prerequisites

If additional device drivers are required (e.g. WinUSB / Npcap / UsbK), place their setup executables or installer packages in this directory and reference them in `installer.iss`.
