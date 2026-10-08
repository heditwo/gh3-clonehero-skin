#ifdef VERTEX
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
uniform vec4 _MainTex_ST;
in vec4 in_POSITION0;
in vec3 in_NORMAL0;
in vec4 in_TANGENT0;
in vec4 in_COLOR0;
in vec2 in_TEXCOORD0;
in vec2 in_TEXCOORD1;
out vec4 surfaceColor;
out vec2 surfaceUv;
out vec2 surfaceLightmapUv;
out vec3 surfaceNormal;
out vec3 surfaceWorld;
out vec4 surfaceTangent;
void main()
{
    mat4 model = mat4(hlslcc_mtx4x4unity_ObjectToWorld[0], hlslcc_mtx4x4unity_ObjectToWorld[1], hlslcc_mtx4x4unity_ObjectToWorld[2], hlslcc_mtx4x4unity_ObjectToWorld[3]);
    mat4 viewProjection = mat4(hlslcc_mtx4x4unity_MatrixVP[0], hlslcc_mtx4x4unity_MatrixVP[1], hlslcc_mtx4x4unity_MatrixVP[2], hlslcc_mtx4x4unity_MatrixVP[3]);
    vec4 world = model * in_POSITION0;
    surfaceWorld = world.xyz;
    gl_Position = viewProjection * world;
    surfaceColor = in_COLOR0;
    surfaceUv = in_TEXCOORD0 * _MainTex_ST.xy + _MainTex_ST.zw;
    surfaceLightmapUv = in_TEXCOORD1;
    surfaceNormal = mat3(model) * in_NORMAL0;
    surfaceTangent = vec4(mat3(model) * in_TANGENT0.xyz, in_TANGENT0.w);
}
#endif
#ifdef FRAGMENT
#version 150
#extension GL_ARB_explicit_attrib_location : require
uniform sampler2D _MainTex;
uniform sampler2D _Gh3Lightmap;
uniform vec4 _Color;
uniform vec4 _Gh3Surface;
uniform sampler2D _Gh3LightData;
uniform vec4 _Gh3LightIndex;
uniform vec4 _Gh3Eye;
uniform vec4 _Gh3Char0;
uniform vec4 _Gh3Char1;
uniform vec4 _Gh3Char2;
uniform vec4 _Gh3Char3;
uniform vec4 _Gh3Char4;
uniform vec4 _Gh3Char5;
uniform sampler2D _Gh3NormalTex;
uniform sampler2D _Gh3SpecularTex;
in vec4 surfaceColor;
in vec2 surfaceUv;
in vec2 surfaceLightmapUv;
in vec3 surfaceNormal;
in vec3 surfaceWorld;
in vec4 surfaceTangent;
layout(location = 0) out vec4 SV_Target0;
// Each lit surface owns one row: ambient, key direction, key color, then four SH coefficients.
vec4 Light(int column)
{
    vec2 size = vec2(textureSize(_Gh3LightData, 0));
    return texture(_Gh3LightData, (vec2(float(column), _Gh3LightIndex.x) + 0.5) / size);
}
float Smooth(float value) { return value * value * (3.0 - 2.0 * value); }
// GH3's SkinShader (mode 3) and the diffuse part of HairShader (mode 4); the projected shadow map is not ported.
vec4 Character(vec4 albedo)
{
    vec3 vertexNormal = normalize(surfaceNormal);
    vec3 normal = vertexNormal;
    vec3 view = normalize(_Gh3Eye.xyz - surfaceWorld);
    // c138: the normal map fades out between 30 and 40 units from the camera.
    float detail = clamp((40.0 - distance(_Gh3Eye.xyz, surfaceWorld)) * 0.1, 0.0, 1.0);
    if (_Gh3Char5.y > 0.5)
    {
        vec3 tangent = normalize(surfaceTangent.xyz - vertexNormal * dot(vertexNormal, surfaceTangent.xyz));
        vec3 bitangent = cross(vertexNormal, tangent) * (surfaceTangent.w < 0.0 ? -1.0 : 1.0);
        vec2 xy = texture(_Gh3NormalTex, surfaceUv).xy * 2.0 - 1.0;
        xy.y = -xy.y;
        vec3 mapped = normalize(vec3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy)))));
        mapped = _Gh3Char5.x * (mapped - vec3(0.0, 0.0, 1.0)) + vec3(0.0, 0.0, 1.0);
        normal = normalize(mix(vertexNormal, mapped.x * tangent + mapped.y * bitangent + mapped.z * vertexNormal, detail));
    }
    vec3 sh = Light(3).xyz + Light(4).xyz * normal.x + Light(5).xyz * normal.y + Light(6).xyz * normal.z;
    vec4 key = Light(2);
    float facing = dot(normal, -Light(1).xyz);
    vec3 base = albedo.rgb * _Gh3Char0.rgb;
    vec3 rim = base + _Gh3Char3.w * (_Gh3Char3.rgb - base);
    float viewFacing = clamp(dot(normal, view), 0.0, 1.0);
    vec3 light;
    float edge;
    vec3 specular = vec3(0.0);
    if (_Gh3Surface.x < 3.5)
    {
        vec3 shadow = key.w * (surfaceColor.rgb - 1.0) + 1.0;
        light = sh + key.rgb * clamp(facing, 0.0, 1.0) * shadow + Light(0).xyz;
        vec3 lit = mix(_Gh3Char2.rgb, vec3(1.0), Smooth(clamp((facing - _Gh3Char1.w) * 1000.0, 0.0, 1.0)));
        light *= mix(_Gh3Char1.rgb, lit, Smooth(clamp(abs(facing - _Gh3Char1.w) / _Gh3Char2.w, 0.0, 1.0)));
        edge = Smooth(clamp((pow(viewFacing, _Gh3Char0.w) * 0.592 + 0.083) * 1.797, 0.0, 1.0));
        albedo.a *= surfaceColor.a;
        if (_Gh3Char4.w > 0.0)
        {
            float highlight = pow(clamp(dot(reflect(-view, normal), -Light(1).xyz), 0.0, 1.0), _Gh3Char4.w);
            vec3 mask = _Gh3Char5.z > 0.5 ? texture(_Gh3SpecularTex, surfaceUv).rgb : vec3(1.0);
            specular = highlight * key.rgb * surfaceColor.rgb * detail * mask * _Gh3Char4.rgb;
        }
    }
    else
    {
        float wrap = clamp((facing + _Gh3Char2.w) / (1.0 + _Gh3Char2.w), 0.0, 1.0);
        light = max(sh + key.rgb * wrap + Light(0).xyz, vec3(_Gh3Char1.w));
        edge = Smooth(clamp((viewFacing * 0.592 + 0.083) * 1.797, 0.0, 1.0));
        // Two-lobe strand highlight; the mask alpha shifts the strand tangent toward the vertex normal.
        vec4 mask = _Gh3Char5.z > 0.5 ? texture(_Gh3SpecularTex, surfaceUv) : vec4(1.0, 1.0, 1.0, 0.5);
        vec3 tangent = normalize(surfaceTangent.xyz - vertexNormal * dot(vertexNormal, surfaceTangent.xyz));
        vec3 strand = cross(vertexNormal, tangent) * (surfaceTangent.w < 0.0 ? -1.0 : 1.0);
        float shift = mask.a * 2.0 - 1.0;
        strand = strand * sqrt(max(0.0, 1.0 - shift * shift)) + vertexNormal * shift;
        vec3 halfway = normalize(view - Light(1).xyz);
        float along = dot(strand, halfway);
        float across = dot(halfway, normalize(halfway - strand * along));
        float grazing = Smooth(clamp((clamp(dot(normal, view), 0.0, 1.0) - _Gh3Char4.w) / (_Gh3Char5.w - _Gh3Char4.w), 0.0, 1.0));
        vec2 exponents = mix(_Gh3Char4.xy, vec2(_Gh3Char4.z), grazing);
        vec3 lobes = pow(max(across, 0.0), exponents.x) * _Gh3Char1.rgb + pow(max(across, 0.0), exponents.y) * _Gh3Char2.rgb;
        specular = lobes * wrap * key.rgb * detail * mask.rgb;
    }
    return vec4(mix(rim, base, edge) * light + specular, albedo.a);
}
void main()
{
    vec4 albedo = texture(_MainTex, surfaceUv) * _Color;
    if (_Gh3Surface.x > 2.5)
    {
        if (_Gh3Surface.y > 0.0 && albedo.a <= _Gh3Surface.y) discard;
        SV_Target0 = Character(albedo);
        return;
    }
    vec4 color = albedo * surfaceColor;
    if (_Gh3Surface.y > 0.0 && color.a <= _Gh3Surface.y) discard;
    if (_Gh3Surface.x > 1.5)
    {
        vec3 normal = normalize(surfaceNormal);
        vec3 lightmap = _Gh3Surface.z > 0.0 ? texture(_Gh3Lightmap, surfaceLightmapUv).rgb : vec3(1.0);
        vec3 ambient = Light(3).xyz + Light(4).xyz * normal.x + Light(5).xyz * normal.y + Light(6).xyz * normal.z;
        vec4 keyColor = Light(2);
        float facing = clamp(dot(normal, -Light(1).xyz), 0.0, 1.0);
        vec3 light = ambient * lightmap + keyColor.xyz * facing * mix(vec3(1.0), lightmap, keyColor.w) + Light(0).xyz;
        color.rgb *= light;
    }
    else if (_Gh3Surface.x > 0.5) color.rgb *= Light(0).xyz;
    color.rgb *= _Gh3Surface.w;
    SV_Target0 = color;
}
#endif
