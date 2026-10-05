#include <algorithm>
#include <cstdint>
#include <new>
#include "libraw/libraw.h"

// Only this small, versioned preview ABI is exported. Sensor unpacking and demosaicing
// are deliberately not exposed; LibRaw layouts remain inside the fixed-header native build.
struct PreviewContext
{
    libraw_data_t* raw;
    std::uint32_t orientation = 0; // unknown: preserve JPEG EXIF
    std::uint32_t channels = 0;
};

struct PreviewInfo
{
    std::uint32_t width, height, channels, bits, format, bytes, orientation;
};

static std::uint32_t orientation_from_flip(unsigned flip)
{
    constexpr unsigned map[] = { 1, 2, 4, 3, 5, 8, 6, 7 };
    return flip < 8 ? map[flip] : 0;
}

extern "C" __declspec(dllexport) int miv_raw_api_version() { return 1; }
extern "C" __declspec(dllexport) int miv_raw_lib_version() { return libraw_versionNumber(); }

extern "C" __declspec(dllexport) PreviewContext* miv_raw_create(progress_callback callback)
{
    auto* context = new (std::nothrow) PreviewContext;
    if (!context) return nullptr;
    context->raw = libraw_init(0);
    if (!context->raw) { delete context; return nullptr; }
    context->raw->rawparams.use_rawspeed = 0;
    context->raw->rawparams.use_dngsdk = 0;
    context->raw->rawparams.max_raw_memory_mb = 64;
    context->raw->rawparams.options = LIBRAW_RAWOPTIONS_USE_PPM16_THUMBS;
    libraw_set_progress_handler(context->raw, callback, nullptr);
    return context;
}

extern "C" __declspec(dllexport) int miv_raw_open(PreviewContext* context, const wchar_t* path)
{
    return libraw_open_wfile(context->raw, path);
}

extern "C" __declspec(dllexport) int miv_raw_unpack_preview(PreviewContext* context)
{
    auto* raw = context->raw;
    int selected = -1;
    std::uint64_t best = 0;
    bool oversized = false;
    for (int i = 0; i < raw->thumbs_list.thumbcount && i < LIBRAW_THUMBNAIL_MAXCOUNT; i++)
    {
        const auto& item = raw->thumbs_list.thumblist[i];
        bool jpeg = item.tformat == LIBRAW_INTERNAL_THUMBNAIL_JPEG;
        bool bitmap = item.tformat == LIBRAW_INTERNAL_THUMBNAIL_PPM || item.tformat == LIBRAW_INTERNAL_THUMBNAIL_PPM16;
        if (!jpeg && !bitmap) continue;
        std::uint64_t pixels = std::uint64_t(item.twidth) * item.theight;
        if (item.tlength > 32 * 1024 * 1024 || (bitmap && pixels * 6 > 32 * 1024 * 1024))
        { oversized = true; continue; }
        if (pixels > 100000000 || item.twidth > 32768 || item.theight > 32768)
        { oversized = true; continue; }
        if (!item.tlength) continue;
        // LibRaw documents unknown thumbnail dimensions as normally the largest preview.
        // Selection stays stable across main/preview/thumbnail requests: SourceSize never changes.
        std::uint64_t score = pixels ? (pixels << 20) + item.tlength : (1ULL << 62) + item.tlength;
        if (selected < 0 || score > best)
        {
            selected = i;
            best = score;
        }
    }
    if (selected >= 0)
    {
        context->orientation = orientation_from_flip(raw->thumbs_list.thumblist[selected].tflip);
        context->channels = (raw->thumbs_list.thumblist[selected].tmisc >> 5) & 7;
        return libraw_unpack_thumb_ex(raw, selected);
    }
    if (raw->thumbs_list.thumbcount > 0) return oversized ? -200001 : LIBRAW_UNSUPPORTED_THUMBNAIL;
    const auto& thumb = raw->thumbnail;
    if (!thumb.tlength) return LIBRAW_NO_THUMBNAIL;
    if (thumb.tlength > 32 * 1024 * 1024 || std::uint64_t(thumb.twidth) * thumb.theight * 16 > 32 * 1024 * 1024)
        return -200001;
    // Non-TIFF formats can lack a list. Do not substitute sensor orientation for an unknown preview orientation.
    context->orientation = 0;
    return libraw_unpack_thumb(raw);
}

extern "C" __declspec(dllexport) int miv_raw_get_preview(PreviewContext* context, PreviewInfo* info, const void** data)
{
    const auto& thumb = context->raw->thumbnail;
    if (context->raw->rawdata.raw_alloc || context->raw->image) return LIBRAW_UNSUPPORTED_THUMBNAIL;
    if (!thumb.thumb || !thumb.tlength) return LIBRAW_NO_THUMBNAIL;
    if (thumb.tlength > 32 * 1024 * 1024) return -200001;
    if (thumb.tformat != LIBRAW_THUMBNAIL_JPEG && thumb.tformat != LIBRAW_THUMBNAIL_BITMAP && thumb.tformat != LIBRAW_THUMBNAIL_BITMAP16)
        return LIBRAW_UNSUPPORTED_THUMBNAIL;
    // LibRaw's PPM16 branch preserves tmisc but does not populate thumbnail.tcolors.
    // Use its selected descriptor, rather than guessing from byte length or sensor metadata.
    const auto channels = thumb.tcolors > 0 ? static_cast<std::uint32_t>(thumb.tcolors) : context->channels;
    *info = { thumb.twidth, thumb.theight, channels,
        thumb.tformat == LIBRAW_THUMBNAIL_BITMAP16 ? 16U : 8U,
        static_cast<std::uint32_t>(thumb.tformat), thumb.tlength, context->orientation };
    *data = thumb.thumb;
    return 0;
}

extern "C" __declspec(dllexport) const char* miv_raw_make(PreviewContext* context) { return context->raw->idata.make; }
extern "C" __declspec(dllexport) const char* miv_raw_model(PreviewContext* context) { return context->raw->idata.model; }
extern "C" __declspec(dllexport) void miv_raw_close(PreviewContext* context)
{
    if (context) { libraw_close(context->raw); delete context; }
}
