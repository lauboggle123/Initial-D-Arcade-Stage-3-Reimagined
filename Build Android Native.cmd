@echo off
setlocal
cd /d "%~dp0"
rem Builds Assets/Plugins/Android/arm64-v8a/libIdas3Unity.so with the NDK bundled
rem in Unity's Android module, and CMake/Ninja from Visual Studio.
if not defined IDAS3_UNITY_EDITOR set "IDAS3_UNITY_EDITOR=%ProgramFiles%\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe"
for %%i in ("%IDAS3_UNITY_EDITOR%") do set "IDAS3_UNITY_DIR=%%~dpi"
set "IDAS3_NDK=%IDAS3_UNITY_DIR%Data\PlaybackEngines\AndroidPlayer\NDK"
if not exist "%IDAS3_NDK%\build\cmake\android.toolchain.cmake" (
  echo Unity Android Build Support with NDK is required: %IDAS3_NDK%
  exit /b 1
)
set "IDAS3_VS="
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "IDAS3_VS=%%i"
set "IDAS3_CMAKE_DIR=%IDAS3_VS%\Common7\IDE\CommonExtensions\Microsoft\CMake"
if not exist "%IDAS3_CMAKE_DIR%\CMake\bin\cmake.exe" (
  echo Visual Studio CMake tools are required.
  exit /b 1
)
rem Ninja invokes clang++ through its 8.3 short name when the NDK path has
rem spaces; clang then infers C mode and omits libc++. Force the C++ driver.
"%IDAS3_CMAKE_DIR%\CMake\bin\cmake.exe" -S Native -B Native/build-android-arm64 -G Ninja ^
  "-DCMAKE_MAKE_PROGRAM=%IDAS3_CMAKE_DIR%\Ninja\ninja.exe" ^
  "-DCMAKE_TOOLCHAIN_FILE=%IDAS3_NDK%\build\cmake\android.toolchain.cmake" ^
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=26 -DANDROID_STL=c++_static ^
  -DCMAKE_BUILD_TYPE=Release "-DCMAKE_SHARED_LINKER_FLAGS=--driver-mode=g++ -Wl,--strip-debug"
if errorlevel 1 exit /b %errorlevel%
"%IDAS3_CMAKE_DIR%\CMake\bin\cmake.exe" --build Native/build-android-arm64 --target Idas3Unity
if errorlevel 1 exit /b %errorlevel%
echo Android native Unity plugin built successfully.
