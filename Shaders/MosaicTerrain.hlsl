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

// rot: 0,1,2,3 -> 0°,90°,180°,270° (CCW)
inline float2 Rotate90(float2 uv, uint rot, float2 rect_size) {
  float2 t = uv / rect_size;
  rot &= 3u;
  uint sw = rot & 1u;
  uint fb = (rot >> 1u) & 1u;

  float2 c = float2(0.5, 0.5);
  float2 v = lerp(t - c, (t - c).yx, (float)sw);

  float2 s = float2(
      1.0 - 2.0 * (float)(sw ^ fb),
      1.0 - 2.0 * (float)fb
  );

  float2 t2 = v * s + c;
  return t2 * rect_size;
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
  SamplerState Sampler,
  out float4 RGBA
) {
  uint blend_data_index = VertexID / 4;
  TerrainIndex indices = _TerrainIndexBuffer[blend_data_index];

  float a_accumulated = 0.0;
  float3 rgb = 0.0;
  
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
    BlendColor(layer, a_accumulated, rgb);
  }
  BlendColor(DefaultBlendColor, a_accumulated, rgb);
  
  RGBA = float4(rgb, a_accumulated);
}

inline void BlendLayers_float(
  uint VertexID,
  float2 TileSize,
  float2 BaseUV,
  float4 DefaultBlendColor,
  Texture2D Texture,
  SamplerState Sampler,
  out float4 RGBA
)
{
  BlendLayers(VertexID, TileSize, BaseUV, DefaultBlendColor, Texture, Sampler, RGBA);
}

#endif // MOSAICTERRAIN_INCLUDED
