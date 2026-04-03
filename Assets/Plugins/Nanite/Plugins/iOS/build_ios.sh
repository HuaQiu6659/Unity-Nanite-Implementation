#!/bin/bash

# Meshoptimizer iOS Build Script
# Please run this script on a macOS machine to build the static library for iOS.
# Requirements: Xcode Command Line Tools, CMake

set -e

echo "Cloning meshoptimizer..."
git clone https://github.com/zeux/meshoptimizer.git
cd meshoptimizer

echo "Building for iOS (arm64)..."
mkdir -p build_ios
cd build_ios

# Run CMake for iOS using Xcode toolchain
cmake .. \
    -DCMAKE_SYSTEM_NAME=iOS \
    -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0 \
    -DMESHOPT_BUILD_SHARED_LIBS=OFF \
    -DCMAKE_XCODE_ATTRIBUTE_ONLY_ACTIVE_ARCH=NO \
    -DCMAKE_IOS_INSTALL_COMBINED=YES

# Build
cmake --build . --config Release

echo "Build complete."
echo "Please copy the resulting 'libmeshoptimizer.a' from 'build_ios/Release-iphoneos/'"
echo "to your Unity project at: Assets/Plugins/Nanite/Plugins/iOS/"
