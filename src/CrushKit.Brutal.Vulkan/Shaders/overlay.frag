#version 450
layout(set = 0, binding = 0) uniform sampler2D overlayTexture;
layout(location = 0) in vec2 textureUv;
layout(location = 1) in vec4 vertexColor;
layout(location = 0) out vec4 outputColor;
void main()
{
    // Both texels (before filtering) and palette tint are premultiplied-linear.
    // ONE/ONE_MINUS_SRC_ALPHA blending; sRGB attachment performs the only encode.
    outputColor = texture(overlayTexture, textureUv) * vertexColor;
}
