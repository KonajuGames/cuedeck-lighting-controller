#version 450

// Lambert under the ambient light and the work light, both sides of every surface lit (its normal turned to face
// the camera), then the haze: a veil thickening with distance, lit by the same light (sLight's volumetric fog, in
// its simplest form). Highlights are rolled off softly; linear in, the sRGB target encodes it.
layout(push_constant) uniform Push
{
    mat4 viewProj;
    vec4 cameraPos;
    vec4 ambient;
    vec4 workDir;
    vec4 workColour;
} pc;

layout(location = 0) in vec3 worldPos;
layout(location = 1) in vec3 normal;
layout(location = 2) in vec4 colour;
layout(location = 3) in float glow;

layout(location = 0) out vec4 outColor;

// Linear up to 0.8, then easing towards 1.
vec3 rolloff(vec3 x)
{
    vec3 over = max(x - 0.8, 0.0);
    return min(x, 0.8) + 0.2 * (1.0 - exp(-over / 0.2));
}

void main()
{
    vec3 n = normalize(normal);
    if (dot(n, pc.cameraPos.xyz - worldPos) < 0.0)
        n = -n;
    vec3 light = pc.ambient.rgb + pc.workColour.rgb * max(dot(n, -pc.workDir.xyz), 0.0);
    vec3 lit = colour.rgb * light + colour.rgb * glow;
    // ambient.w: the haze's density per metre.
    float veil = 1.0 - exp(-pc.ambient.w * distance(pc.cameraPos.xyz, worldPos));
    // What the haze scatters back: a fraction of the light through it (Godot's fog, albedo 0.92, scatters much of it
    // elsewhere).
    vec3 haze = 0.2 * (pc.ambient.rgb + pc.workColour.rgb);
    outColor = vec4(rolloff(mix(lit, haze, veil)), 1.0);
}
