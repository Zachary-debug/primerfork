// res://shaders/SurfacePlot_Height.gdshader
shader_type spatial;
render_mode cull_disabled;

uniform sampler2D height_tex_a : source_color;
uniform sampler2D height_tex_b : source_color;
uniform float mix_factor : hint_range(0,1) = 0.0;
uniform float cutoff : hint_range(0,1) = 1.0;

uniform vec2 x_range = vec2(0.0, 1.0);
uniform vec2 z_range = vec2(0.0, 1.0);
uniform vec3 pos_scale = vec3(1.0);
uniform vec3 pos_offset = vec3(0.0);

uniform vec4 albedo_color : source_color = vec4(1.0);

float sample_height(vec2 uv) {
    float ha = texture(height_tex_a, uv).r;
    float hb = texture(height_tex_b, uv).r;
    return mix(ha, hb, mix_factor);
}

void vertex() {
    // Map UV → data space
    float x_ds = mix(x_range.x, x_range.y, UV.x);
    float z_ds = mix(z_range.x, z_range.y, UV.y);
    float y_ds = sample_height(UV);

    vec3 pos = vec3(x_ds, y_ds, z_ds);
    VERTEX = pos * pos_scale + pos_offset;
}

void fragment() {
    if (UV.x > cutoff) { discard; }
    ALBEDO = albedo_color.rgb;
    ALPHA = albedo_color.a;
}
