param (
    [Parameter(Mandatory = $true)]
    [string]$LibVlcRoot,

    [Parameter(Mandatory = $true)]
    [string]$TargetArch
)

# Trims the VideoLAN.LibVLC.Windows payload down to what motion photo playback needs:
#  1. Sibling architecture folders (libvlc\win-*) other than the publish target arch —
#     the NuGet package copies all architectures even for a RID-specific publish.
#  2. Link-time-only import libraries (*.lib) in the arch root.
#  3. lua\ scripts (the lua plugin is not kept) and hrtfs\ data (the headphone
#     spatializer audio filter is not kept).
#  4. Plugin DLLs not on the whitelist below.
#
# The whitelist was determined empirically via `libvlc -vv` module logs and verified by
# playing h264/aac mp4 files through the software video callbacks (StreamMediaInput),
# including the 1080p d3d11va/dxva2 hardware decode path:
#   imem access, mp4 demux, avcodec decoder (+ hw accel), swscale/yuvp/chain +
#   d3d11/d3d9 video converters, vmem video output, wasapi/mmdevice audio chain.
# Missing whitelist entries are fine (e.g. win-arm64 has no d3d9/dxva2 plugins).
$whitelist = @(
    "access\libaccess_imem_plugin.dll",
    "access\libimem_plugin.dll",
    "audio_filter\libscaletempo_plugin.dll",
    "audio_filter\libtrivial_channel_mixer_plugin.dll",
    "audio_filter\libugly_resampler_plugin.dll",
    "audio_mixer\libfloat_mixer_plugin.dll",
    "audio_output\libmmdevice_plugin.dll",
    "audio_output\libwasapi_plugin.dll",
    "codec\libavcodec_plugin.dll",
    "codec\libd3d11va_plugin.dll",
    "codec\libdxva2_plugin.dll",
    "d3d11\libdirect3d11_filters_plugin.dll",
    "d3d9\libdirect3d9_filters_plugin.dll",
    "demux\libmp4_plugin.dll",
    "keystore\libmemory_keystore_plugin.dll",
    "logger\libconsole_logger_plugin.dll",
    "stream_filter\libprefetch_plugin.dll",
    "stream_filter\librecord_plugin.dll",
    "video_chroma\libchain_plugin.dll",
    "video_chroma\libswscale_plugin.dll",
    "video_chroma\libyuvp_plugin.dll",
    "video_output\libvmem_plugin.dll"
)

if (-not (Test-Path $LibVlcRoot)) {
    Write-Warning "libvlc directory not found: $LibVlcRoot"
    return
}

# 1. Remove sibling architecture folders (e.g. win-x86, and win-arm64 in an x64 publish)
Get-ChildItem -Path $LibVlcRoot -Directory -Filter "win-*" | Where-Object {
    $_.Name -ne $TargetArch
} | ForEach-Object {
    $sizeMB = ((Get-ChildItem $_.FullName -Recurse -File) | Measure-Object Length -Sum).Sum / 1MB
    Remove-Item -Path $_.FullName -Recurse -Force
    Write-Host ("libvlc trim: removed sibling arch {0} ({1:N1} MB)" -f $_.Name, $sizeMB)
}

$archDir = Join-Path -Path $LibVlcRoot -ChildPath $TargetArch
if (-not (Test-Path $archDir)) {
    Write-Warning "libvlc arch directory not found: $archDir"
    return
}

# 2. Remove link-time-only import libraries
Get-ChildItem -Path $archDir -Filter *.lib | ForEach-Object {
    Remove-Item -Path $_.FullName -Force
}

# 3. Remove lua scripts and HRTF data (their plugins/filters are not kept)
foreach ($subDir in "lua", "hrtfs") {
    $path = Join-Path -Path $archDir -ChildPath $subDir
    if (Test-Path $path) {
        Remove-Item -Path $path -Recurse -Force
    }
}

# 4. Remove plugin DLLs not on the whitelist
$pluginsDir = Join-Path -Path $archDir -ChildPath "plugins"
if (-not (Test-Path $pluginsDir)) {
    Write-Warning "libvlc plugins directory not found: $pluginsDir"
    return
}

$removed = 0
Get-ChildItem -Path $pluginsDir -Recurse -Filter *.dll | ForEach-Object {
    $relativePath = $_.FullName.Substring($pluginsDir.Length + 1)
    if ($whitelist -notcontains $relativePath) {
        Remove-Item -Path $_.FullName -Force
        $removed++
    }
}

# Drop plugin directories left empty
Get-ChildItem -Path $pluginsDir -Directory | Where-Object {
    -not (Get-ChildItem -Path $_.FullName -Recurse -File)
} | Remove-Item -Recurse -Force

Write-Host "libvlc trim ($TargetArch): removed $removed plugin DLLs, kept $($whitelist.Count) (motion photo playback set)"
