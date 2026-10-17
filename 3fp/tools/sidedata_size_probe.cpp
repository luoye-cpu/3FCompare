// Probe: the numbers the kind-3 side-data ring must be sized against.
// Prints the AVFrameSideDataType enum values a host passes to
// FFF3FP_CopyFrameSideDataAfter, and sizeof the public structs those side
// data blobs are read through. Nothing here is a guess: run this, read the
// output, size the ring from it.
//
// Build: py 3fp/tools/build_probe.py sidedata_size_probe  (the headers come
// from 3fp/kernel/third_party/ffmpeg/include, the same tree the kernel builds
// against; verified identical to third_party/ffmpeg for these structs).
//
// Note AVDOVIMetadata is a self-relative blob (header/mapping/color/extension
// blocks reached by offsets), so its sizeof is only the fixed header; the real
// blob size is computed at runtime. Print the component structs too, so the
// ring cap can be justified from the parts.
#include <cstdio>

#include "libavutil/frame.h"
#include "libavutil/dovi_meta.h"
#include "libavutil/hdr_dynamic_metadata.h"
#include "libavutil/hdr_dynamic_vivid_metadata.h"
#include "libavutil/mastering_display_metadata.h"
#include "libavutil/film_grain_params.h"

#define SHOW(x)   std::printf("%-38s %d\n", #x, (int)(x))
#define SHOWSZ(x) std::printf("%-38s %zu\n", #x, sizeof(x))

int main()
{
    std::printf("== AVFrameSideDataType values ==\n");
    SHOW(AV_FRAME_DATA_PANSCAN);
    SHOW(AV_FRAME_DATA_A53_CC);
    SHOW(AV_FRAME_DATA_STEREO3D);
    SHOW(AV_FRAME_DATA_MATRIXENCODING);
    SHOW(AV_FRAME_DATA_DOWNMIX_INFO);
    SHOW(AV_FRAME_DATA_REPLAYGAIN);
    SHOW(AV_FRAME_DATA_DISPLAYMATRIX);
    SHOW(AV_FRAME_DATA_AFD);
    SHOW(AV_FRAME_DATA_MOTION_VECTORS);
    SHOW(AV_FRAME_DATA_SKIP_SAMPLES);
    SHOW(AV_FRAME_DATA_AUDIO_SERVICE_TYPE);
    SHOW(AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
    SHOW(AV_FRAME_DATA_GOP_TIMECODE);
    SHOW(AV_FRAME_DATA_SPHERICAL);
    SHOW(AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
    SHOW(AV_FRAME_DATA_ICC_PROFILE);
    SHOW(AV_FRAME_DATA_S12M_TIMECODE);
    SHOW(AV_FRAME_DATA_DYNAMIC_HDR_PLUS);
    SHOW(AV_FRAME_DATA_REGIONS_OF_INTEREST);
    SHOW(AV_FRAME_DATA_VIDEO_ENC_PARAMS);
    SHOW(AV_FRAME_DATA_SEI_UNREGISTERED);
    SHOW(AV_FRAME_DATA_FILM_GRAIN_PARAMS);
    SHOW(AV_FRAME_DATA_DETECTION_BBOXES);
    SHOW(AV_FRAME_DATA_DOVI_RPU_BUFFER);
    SHOW(AV_FRAME_DATA_DOVI_METADATA);
    SHOW(AV_FRAME_DATA_DYNAMIC_HDR_VIVID);
    SHOW(AV_FRAME_DATA_AMBIENT_VIEWING_ENVIRONMENT);
    SHOW(AV_FRAME_DATA_VIDEO_HINT);
    SHOW(AV_FRAME_DATA_LCEVC);

    std::printf("== struct sizes (bytes) ==\n");
    SHOWSZ(AVFrameSideData);
    SHOWSZ(AVDOVIDecoderConfigurationRecord);
    SHOWSZ(AVDOVIMetadata);
    SHOWSZ(AVDOVIRpuDataHeader);
    SHOWSZ(AVDOVIReshapingCurve);
    SHOWSZ(AVDOVIDataMapping);
    SHOWSZ(AVDOVINLQParams);
    SHOWSZ(AVDOVIColorMetadata);
    SHOWSZ(AVDOVIDmData);
    SHOWSZ(AVDynamicHDRPlus);
    SHOWSZ(AVDynamicHDRVivid);
    SHOWSZ(AVMasteringDisplayMetadata);
    SHOWSZ(AVContentLightMetadata);
    SHOWSZ(AVFilmGrainParams);
    return 0;
}
