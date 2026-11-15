#version 450

// The visualizer's glow (Godot's, as sLight set it up), one pass of it per draw, chosen by mode:
//   0: the bright parts of the HDR picture picked out and shrunk to half size (Godot's first glow pass);
//   1: an image shrunk to half size again, blurring as it goes;
//   2: a smaller image enlarged (tent-filtered) and weighted, added to one this size, weighted;
//   3: the picture's highlights rolled off, and the glow, rolled off the same way, laid over it with a Screen blend.
// Matches Bloom.Push.
layout(push_constant) uniform Push
{
    vec4 mode;     // x: the mode
    vec4 bright;   // x: the HDR threshold, y: the HDR scale, z: the bloom, w: the luminance cap
    vec4 weights;  // x: tex0's weight, y: tex1's weight, z: the glow's intensity
} pc;

layout(set = 0, binding = 0) uniform sampler2D tex0; // the source, or the smaller image
layout(set = 0, binding = 1) uniform sampler2D tex1; // the image this size

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;

// How much of a pixel glows: smoothly more above the threshold, never less than the bloom (Godot's feedback).
vec3 feedback(vec3 c)
{
    float l = max(c.r, max(c.g, c.b));
    float f = max(smoothstep(pc.bright.x, pc.bright.x + pc.bright.y, l), pc.bright.z);
    return min(c * f, vec3(pc.bright.w));
}

vec3 tap(vec2 p, bool first)
{
    vec3 c = texture(tex0, p).rgb;
    return first ? feedback(c) : c;
}

// Thirteen taps, as five overlapping boxes, shrinking without shimmering.
vec3 shrink(bool first)
{
    vec2 t = 1.0 / vec2(textureSize(tex0, 0));
    vec3 a = tap(uv + t * vec2(-2, -2), first), b = tap(uv + t * vec2(0, -2), first), c = tap(uv + t * vec2(2, -2), first);
    vec3 d = tap(uv + t * vec2(-1, -1), first), e = tap(uv + t * vec2(1, -1), first);
    vec3 f = tap(uv + t * vec2(-2, 0), first), g = tap(uv, first), h = tap(uv + t * vec2(2, 0), first);
    vec3 i = tap(uv + t * vec2(-1, 1), first), j = tap(uv + t * vec2(1, 1), first);
    vec3 k = tap(uv + t * vec2(-2, 2), first), l = tap(uv + t * vec2(0, 2), first), m = tap(uv + t * vec2(2, 2), first);
    return (d + e + i + j) * 0.125 + (a + b + f + g + b + c + g + h + f + g + k + l + g + h + l + m) * 0.03125;
}

// A 3 x 3 tent over the smaller image, enlarging it smoothly.
vec3 enlarge()
{
    vec2 t = 1.0 / vec2(textureSize(tex0, 0));
    vec3 s = texture(tex0, uv).rgb * 4.0;
    s += (texture(tex0, uv + vec2(t.x, 0)).rgb + texture(tex0, uv - vec2(t.x, 0)).rgb
        + texture(tex0, uv + vec2(0, t.y)).rgb + texture(tex0, uv - vec2(0, t.y)).rgb) * 2.0;
    s += texture(tex0, uv + t).rgb + texture(tex0, uv - t).rgb
        + texture(tex0, uv + vec2(t.x, -t.y)).rgb + texture(tex0, uv + vec2(-t.x, t.y)).rgb;
    return s / 16.0;
}

// Linear up to 0.8, then easing towards 1 (the scene's highlight roll-off).
vec3 rolloff(vec3 x)
{
    vec3 over = max(x - 0.8, 0.0);
    return min(x, 0.8) + 0.2 * (1.0 - exp(-over / 0.2));
}

vec3 toSrgb(vec3 c)
{
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

vec3 toLinear(vec3 c)
{
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(0.04045, c));
}

void main()
{
    int mode = int(pc.mode.x);
    vec3 c;
    if (mode == 0)
        c = shrink(true);
    else if (mode == 1)
        c = shrink(false);
    else if (mode == 2)
        c = enlarge() * pc.weights.x + texture(tex1, uv).rgb * pc.weights.y;
    else
    {
        // As Godot: both tone-mapped, then screened together in sRGB; the target encodes it again.
        vec3 picture = toSrgb(rolloff(max(texture(tex1, uv).rgb, 0.0)));
        vec3 glow = toSrgb(rolloff(enlarge() * pc.weights.z));
        c = toLinear(clamp(picture + glow - picture * glow, 0.0, 1.0));
    }
    outColor = vec4(c, 1.0);
}
