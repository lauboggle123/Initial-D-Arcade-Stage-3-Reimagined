#pragma once
// Portable replacements for the DirectXMath calls used by the Unity scene
// capture. Layout and conventions match XMFLOAT4X4: row-major storage, row
// vectors (v*M), so the bytes copied into frame constants are unchanged.
#include "../src/math_types.h"
#include <cmath>

namespace idas3::portable {
struct Matrix4 { float m[4][4]{}; };
static_assert(sizeof(Matrix4)==64);

// XMMatrixLookToLH followed by the RH variant's negated direction.
inline Matrix4 lookAt(Vec3 eye,Vec3 target,Vec3 up,bool leftHanded){
    const Vec3 r2=normalized(leftHanded?target-eye:eye-target);
    const Vec3 r0=normalized(cross(up,r2));
    const Vec3 r1=cross(r2,r0);
    const Vec3 negEye=-eye;
    Matrix4 out;
    out.m[0][0]=r0.x;out.m[0][1]=r1.x;out.m[0][2]=r2.x;out.m[0][3]=0;
    out.m[1][0]=r0.y;out.m[1][1]=r1.y;out.m[1][2]=r2.y;out.m[1][3]=0;
    out.m[2][0]=r0.z;out.m[2][1]=r1.z;out.m[2][2]=r2.z;out.m[2][3]=0;
    out.m[3][0]=dot(r0,negEye);out.m[3][1]=dot(r1,negEye);out.m[3][2]=dot(r2,negEye);out.m[3][3]=1;
    return out;
}

// XMMatrixPerspectiveFovLH / XMMatrixPerspectiveFovRH.
inline Matrix4 perspective(float verticalFov,float aspect,float nearClip,float farClip,bool leftHanded){
    const float half=verticalFov*.5f;
    const float height=std::cos(half)/std::sin(half),width=height/aspect;
    const float range=leftHanded?farClip/(farClip-nearClip):farClip/(nearClip-farClip);
    Matrix4 out;
    out.m[0][0]=width;
    out.m[1][1]=height;
    out.m[2][2]=range;out.m[2][3]=leftHanded?1.f:-1.f;
    out.m[3][2]=leftHanded?-range*nearClip:range*nearClip;
    return out;
}

// XMMatrixMultiply(a,b): a then b for row vectors.
inline Matrix4 multiply(const Matrix4& a,const Matrix4& b){
    Matrix4 out;
    for(int row=0;row<4;++row)for(int column=0;column<4;++column){
        float sum=0;
        for(int k=0;k<4;++k)sum+=a.m[row][k]*b.m[k][column];
        out.m[row][column]=sum;
    }
    return out;
}
}
