#version 450
// 文本格栅片元着色器(Vulkan 版): 背景层输出不透明纯色, 字形层用图集 R8 的 alpha 做覆盖。
// 层号由顶点着色器按实例下标算出并以 flat 传出(不再走 push constant) —— 两次 draw 合成一次后
// 片元着色器仍需知道自己属于哪一层。

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) flat in float vLayer;

layout(location = 0) out vec4 FragColor;

layout(set = 0, binding = 0) uniform sampler2D uAtlas;

void main()
{
    FragColor = vLayer < 0.5
        ? vec4(vColor.rgb, 1.0)
        : vec4(vColor.rgb, texture(uAtlas, vUv).r);
}
