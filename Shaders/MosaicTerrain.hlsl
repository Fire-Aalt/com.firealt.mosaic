#ifndef MOSAICTERRAIN_INCLUDED
#define MOSAICTERRAIN_INCLUDED

struct TerrainTile {
  float2 offset;
  uint flags; // packed: flipX(1) | flipY(1) | rot(2)
};

struct TerrainIndex {
  uint start_index;
  uint end_index;
};

StructuredBuffer<TerrainTile> _TerrainTileBuffer;
StructuredBuffer<TerrainIndex> _TerrainIndexBuffer;

inline uint GetFlipX(uint flags)  { return (flags) & 1u; }
inline uint GetFlipY(uint flags)  { return (flags >> 1u) & 1u; }
inline uint GetRot(uint flags)    { return (flags >> 2u) & 3u; }

// flipX: 0/1, flipY: 0/1
inline float2 Flip(float2 uv, float flip_x, float flip_y, float2 rect_size) {
  float2 t = uv / rect_size;
  float2 c = float2(0.5, 0.5);
  
  float2 s = float2(
      1.0 - 2.0 * flip_x,
      1.0 - 2.0 * flip_y
  );
  
  float2 t2 = (t - c) * s + c;
  return t2 * rect_size;
}

// Match the source-tile mesh rotation in the default XZ terrain basis. The
// terrain mesh stays axis aligned, so this rotates the sampled source UV into
// that mesh.
inline float2 Rotate90(float2 uv, uint rot, float2 rect_size) {
  float2 t = uv / rect_size;
  rot &= 3u;

  if (rot == 1u)
    return float2(1.0 - t.y, t.x) * rect_size;
  if (rot == 2u)
    return float2(1.0 - t.x, 1.0 - t.y) * rect_size;
  if (rot == 3u)
    return float2(t.y, 1.0 - t.x) * rect_size;
  return uv;
}

// The sampled normal is expressed in the rotated source UV basis. Convert it
// back into the axis-aligned terrain basis with the inverse UV rotation. This
// is the normal-space counterpart of Rotate90 above.
inline float2 RotateNormalXY(float2 normal_xy, uint rot) {
  rot &= 3u;

  if (rot == 1u)
    return float2(normal_xy.y, -normal_xy.x);
  if (rot == 2u)
    return -normal_xy;
  if (rot == 3u)
    return float2(-normal_xy.y, normal_xy.x);
  return normal_xy;
}

// Unity's normal-map import can decode the generated neutral 128/255 channel
// into the neighbouring signed 8-bit bin. Keep a neutral layer neutral so a
// random result mirror cannot turn that representation error into lighting.
inline float3 UnpackTilemapNormal(float4 packed_normal) {
  float3 normal = UnpackNormal(packed_normal);
  const float NEUTRAL_BIN_CUTOFF = 1.5 / 255.0;

  return all(abs(normal.xy) < NEUTRAL_BIN_CUTOFF)
      ? float3(0.0, 0.0, 1.0)
      : normal;
}

// Read all params for an id
inline void ReadTileParams(uint data_index,
                           out float2 offset,
                           out float flip_x,
                           out float flip_y,
                           out uint rot) {
  TerrainTile p = _TerrainTileBuffer[data_index];
  offset = p.offset;
  uint flags = p.flags;
  flip_x = GetFlipX(flags);
  flip_y = GetFlipY(flags);
  rot = GetRot(flags);
}

inline void ComputeLayer(
  uint index,
  float2 tile_size,
  float2 quad_uv,
  out float2 uv
) {
  float2 offset;
  float flip_x, flip_y;
  uint rot;
  ReadTileParams(index, offset, flip_x, flip_y, rot);

  uv = Rotate90(quad_uv, rot, tile_size);
  uv = Flip(uv, flip_x, flip_y, tile_size);
  uv += offset;
}

void BlendColor(float4 color, inout float a_accumulated, inout float3 rgb)
{
  float a_effective = saturate(color.a); 
  rgb += (1.0 - a_accumulated) * (color.rgb * a_effective);
  a_accumulated += (1.0 - a_accumulated) * a_effective;
}

inline void BlendLayers(
  uint VertexID,
  float2 TileSize,
  float2 BaseUV,
  float4 DefaultBlendColor,
  Texture2D Texture,
  Texture2D NormalTexture,
  SamplerState Sampler,
  SamplerState NormalSampler,
  out float4 RGBA,
  out float3 Normal
) {
  uint blend_data_index = VertexID / 4;
  TerrainIndex indices = _TerrainIndexBuffer[blend_data_index];

  float a_accumulated = 0.0;
  float3 rgb = 0.0;
  float3 normal_accumulated = 0.0;
  
  #if MOSAIC_BLEND_128
  [unroll(10)]
  #else
  [unroll(5)]
  #endif
  for (uint index = indices.start_index; index < indices.end_index; ++index)
  {
    float2 uv;
    ComputeLayer(index, TileSize, BaseUV, uv);
    
    float4 layer = SAMPLE_TEXTURE2D(Texture, Sampler, uv);
    float3 layer_normal = UnpackTilemapNormal(SAMPLE_TEXTURE2D(NormalTexture, NormalSampler, uv));

    uint flags = _TerrainTileBuffer[index].flags;
    float2 flip = float2(GetFlipX(flags), GetFlipY(flags));
    layer_normal.xy *= 1.0 - 2.0 * flip;

    uint rot = GetRot(flags);
    layer_normal.xy = RotateNormalXY(layer_normal.xy, rot);

    float normal_weight = (1.0 - a_accumulated) * saturate(layer.a);
    normal_accumulated += layer_normal * normal_weight;
    BlendColor(layer, a_accumulated, rgb);
  }

  normal_accumulated.z += (1.0 - a_accumulated) * saturate(DefaultBlendColor.a);
  BlendColor(DefaultBlendColor, a_accumulated, rgb);
  
  RGBA = float4(rgb, a_accumulated);
  float normal_length_squared = dot(normal_accumulated, normal_accumulated);
  Normal = normal_length_squared > 0.0
    ? normal_accumulated * rsqrt(normal_length_squared)
    : float3(0.0, 0.0, 1.0);
}

#endif // MOSAICTERRAIN_INCLUDED
