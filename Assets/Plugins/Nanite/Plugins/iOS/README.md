# iOS Build Instructions

Due to Apple's restrictions, building native iOS binaries (`.a` or `.framework`) requires the macOS SDK and Xcode toolchain, which are not available in standard Linux CI/Sandbox environments.

We have provided a script `build_ios.sh` to help you compile `meshoptimizer` for iOS.

## Steps:
1. Copy the `build_ios.sh` script to your macOS computer.
2. Open Terminal and run:
   ```bash
   chmod +x build_ios.sh
   ./build_ios.sh
   ```
3. After the build completes, grab the `libmeshoptimizer.a` file.
4. Place `libmeshoptimizer.a` into this folder (`Assets/Plugins/Nanite/Plugins/iOS/`) in your Unity project.

Unity will automatically link the static library when you build your iOS Xcode project.