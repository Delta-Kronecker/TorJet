# TorJet Android native runtime.

This directory is populated automatically by the GitHub Actions workflow
`.github/workflows/build-android.yml` (job `native`), which:
  1. cross-compiles tor (with TorJet's conflux extensions) + geoip/geoip6 for
     aarch64 using the Android NDK (`android/scripts/build-android-tor.sh`),
  2. cross-compiles the obfs4/webtunnel/snowflake transports for Android,
  3. copies the resulting binaries, geoip files and bridge lists here.

Do not commit the binaries — they are ~20-50 MB and regenerated per build.
The app copies these files from assets into its private dir and chmod +x's the
executables at first launch (see `TorController.extractRuntimeFiles`).
