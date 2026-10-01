#include "FrameProtocol.h"
#include <cstdio>
#include <cstdlib>
#include <limits>
using namespace MateeFrame;
void require(bool pass) { if (!pass) { std::fprintf(stderr,"Presenter protocol assertion failed\n"); std::abort(); } }
int main() {
    QByteArray h(HeaderSize,0);
    auto integer=[&](int o,quint32 v) { qToLittleEndian(v,h.data()+o); };
    auto number=[&](int o,float v) { quint32 bits; std::memcpy(&bits,&v,4); integer(o,bits); };
    std::memcpy(h.data(),"MATEEFRM",8); integer(8,3);integer(12,4);integer(16,4);integer(20,16);integer(24,1);integer(28,64);
    number(56,-1200.5f);number(60,-30.25f);number(64,3);number(68,3);
    h[80]='a';number(208,-1280);number(212,-100);number(216,1280);number(220,1024);number(224,1.25f);
    require(validHeader(h));require(f32(h,56)==-1200.5f);
    integer(8,2);require(!validHeader(h));integer(8,3);
    integer(32,2);require(!validHeader(h));integer(32,0);
    number(64,std::numeric_limits<float>::quiet_NaN());require(!validHeader(h));number(64,3);
    require(!validHeader(h.left(100)));
    QImage image(4,4,QImage::Format_RGBA8888);image.fill(Qt::white);
    image.setPixelColor(1,1,Qt::transparent);
    auto region=inputRegion(image,QSize(3,3),false);
    require(!region.contains(QPoint(0,0)));require(!region.contains(QPoint(1,1)));require(region.contains(QPoint(2,2)));
    image.fill(Qt::transparent);require(inputRegion(image,QSize(3,3),false).isEmpty());
    image.setPixelColor(0,0,Qt::white);require(inputRegion(image,QSize(4,4),true).contains(QPoint(0,3)));
    std::puts("Presenter protocol: version, geometry, malformed header, fractional input masks, vertical flip passed");
}
