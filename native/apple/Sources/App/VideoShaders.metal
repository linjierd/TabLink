#include <metal_stdlib>
using namespace metal;

struct VideoVertex { float4 position [[position]]; float2 uv; };
vertex VideoVertex tablinkVertex(uint id [[vertex_id]], constant float2 &fit [[buffer(0)]]) {
    const float2 positions[] = {float2(-1, 1), float2(-1, -1), float2(1, 1), float2(1, -1)};
    const float2 uv[] = {float2(0, 0), float2(0, 1), float2(1, 0), float2(1, 1)};
    return {float4(positions[id] * fit, 0, 1), uv[id]};
}
fragment float4 tablinkFragment(VideoVertex input [[stage_in]], texture2d<float> image [[texture(0)]]) {
    constexpr sampler linearSampler(coord::normalized, address::clamp_to_edge, filter::linear);
    return image.sample(linearSampler, input.uv);
}
