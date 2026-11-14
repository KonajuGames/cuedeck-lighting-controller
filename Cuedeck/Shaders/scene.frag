#version 450

// Lambert under the ambient light, the work light and every lit head's spotlight (Godot's fall-off with distance and
// towards the cone's edge, faded out far from the camera as sLight's were), both sides of every surface lit (its normal turned to face
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

// Matches Scene3D.Light.
struct Light
{
    vec4 position;  // xyz; w: range
    vec4 direction; // xyz, which way it shines; w: the cosine of its cone's half angle
    vec4 colour;    // rgb: colour times energy; w: its decay with distance
    vec4 extra;     // x: how soft the cone's edge is
};

layout(std430, set = 0, binding = 1) readonly buffer Lights
{
    Light lights[];
};

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
    // cameraPos.w: how many spotlights.
    int count = int(pc.cameraPos.w);
    for (int i = 0; i < count; i++)
    {
        Light l = lights[i];
        vec3 toLight = l.position.xyz - worldPos;
        float d = length(toLight);
        if (d >= l.position.w)
            continue;
        vec3 dir = toLight / d;
        // Godot's get_omni_attenuation, then its spot cone.
        float nd = d / l.position.w;
        nd *= nd;
        nd *= nd;
        nd = max(1.0 - nd, 0.0);
        float att = nd * nd * pow(max(d, 0.0001), -l.colour.w);
        float cutoff = l.direction.w;
        float scos = max(dot(-dir, l.direction.xyz), cutoff);
        float rim = max(0.0001, (1.0 - scos) / (1.0 - cutoff));
        att *= 1.0 - pow(rim, l.extra.x);
        // Faded from 24 m to 34 m away from the camera (sLight's distance fade).
        att *= 1.0 - clamp((distance(pc.cameraPos.xyz, l.position.xyz) - 24.0) / 10.0, 0.0, 1.0);
        // Godot scales a spot's energy by pi, which its Lambert's 1 / pi takes back out.
        light += l.colour.rgb * att * max(dot(n, dir), 0.0);
    }
    vec3 lit = colour.rgb * light + colour.rgb * glow;
    // ambient.w: the haze's density per metre.
    float veil = 1.0 - exp(-pc.ambient.w * distance(pc.cameraPos.xyz, worldPos));
    // What the haze scatters back: a fraction of the light through it (Godot's fog, albedo 0.92, scatters much of it
    // elsewhere).
    vec3 haze = 0.2 * (pc.ambient.rgb + pc.workColour.rgb);
    outColor = vec4(rolloff(mix(lit, haze, veil)), 1.0);
}
