#ifndef MOSAICTILEMAP_INCLUDED
#define MOSAICTILEMAP_INCLUDED

// Unity's normal-map import can decode the generated neutral 128/255 channel
// into the neighbouring signed 8-bit bin. Keep a neutral layer neutral so a
// random result mirror cannot turn that representation error into lighting.
inline float3 UnpackTilemapNormal(float4 packed_normal) {
  float3 normal = UnpackNormal(packed_normal);
  const float center_bin = 1.0 / 255.0;
  if (all(abs(normal.xy) <= center_bin))
    return float3(0.0, 0.0, 1.0);
  return normal;
}

inline float3 SampleTilemapNormalMap(Texture2D Texture, float2 UV, float NormalStrength, SamplerState Sampler) {
  float3 normal = UnpackTilemapNormal(SAMPLE_TEXTURE2D(Texture, Sampler, UV));
  return normal;
}

#endif // MOSAICTERRAIN_INCLUDED
