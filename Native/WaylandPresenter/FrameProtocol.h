#pragma once
#include <QByteArray>
#include <QImage>
#include <QRegion>
#include <QSize>
#include <QtEndian>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

namespace MateeFrame {
constexpr int HeaderSize=256;
inline quint32 u32(const QByteArray &b, qsizetype o) { return qFromLittleEndian<quint32>(b.constData()+o); }
inline quint64 u64(const QByteArray &b, qsizetype o) { return qFromLittleEndian<quint64>(b.constData()+o); }
inline float f32(const QByteArray &b, qsizetype o) { quint32 bits=u32(b,o); float value; std::memcpy(&value,&bits,sizeof(value)); return value; }
inline bool validHeader(const QByteArray &h) {
    if (h.size()!=HeaderSize || std::memcmp(h.constData(),"MATEEFRM",8) || u32(h,8)!=3) return false;
    const auto w=u32(h,12), height=u32(h,16), stride=u32(h,20);
    if (!w || !height || w>8192 || height>8192 || stride!=w*4 || u32(h,24)!=1 || u32(h,28)!=stride*height || u32(h,32)>1) return false;
    for (int offset : {56,60,64,68,208,212,216,220,224}) if (!std::isfinite(f32(h,offset))) return false;
    for (int offset : {64,68,216,220}) if (f32(h,offset)<1 || f32(h,offset)>65536) return false;
    if (f32(h,64)>8192 || f32(h,68)>8192 || f32(h,224)<=0) return false;
    return h[80]!=0 && std::memchr(h.constData()+80,0,128)!=nullptr;
}
// A logical input cell is accepted only if every covered source pixel is opaque.
// This deliberately shrinks edge cells, rather than intercepting a masked window.
inline QRegion inputRegion(const QImage &frame, QSize size, bool flip) {
    QRegion next;
    if (frame.isNull() || size.isEmpty()) return next;
    std::vector<QRect> runs;
    std::vector<int> starts(size.width()), ends(size.width());
    for (int x=0;x<size.width();++x) {
        starts[x]=x*frame.width()/size.width();
        ends[x]=std::min(frame.width(),((x+1)*frame.width()+size.width()-1)/size.width());
    }
    for (int y=0;y<size.height();++y) {
        const int y0=y*frame.height()/size.height();
        const int y1=std::min(frame.height(),((y+1)*frame.height()+size.height()-1)/size.height());
        int run=-1;
        for (int x=0;x<=size.width();++x) {
            bool occupied=x<size.width();
            const int x0=occupied?starts[x]:0, x1=occupied?ends[x]:0;
            for (int py=y0;py<y1 && occupied;++py) {
                const uchar *line=frame.constScanLine(flip?frame.height()-1-py:py);
                for (int px=x0;px<x1;++px) if (line[px*4+3]<8) { occupied=false; break; }
            }
            if (occupied && run<0) run=x;
            if (!occupied && run>=0) { runs.emplace_back(run,y,x-run,1); run=-1; }
        }
    }
    if (!runs.empty()) next.setRects(runs.data(),int(runs.size()));
    return next;
}
}
