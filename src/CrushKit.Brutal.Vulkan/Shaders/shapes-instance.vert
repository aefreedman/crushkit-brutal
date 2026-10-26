#version 450
layout(location=0) in vec2 localPosition;
layout(location=1) in vec2 origin;
layout(location=2) in vec2 axisX;
layout(location=3) in vec2 axisY;
layout(location=4) in vec4 color;
layout(push_constant) uniform Frame { vec2 extent; } frame;
layout(location=0) out vec4 vertexColor;
void main()
{
    vec2 position = origin + axisX * localPosition.x + axisY * localPosition.y;
    gl_Position = vec4(2.0 * position / frame.extent - 1.0, 0.0, 1.0);
    vertexColor = color;
}
