#include "FrameProtocol.h"
#include <LayerShellQt/Shell>
#include <LayerShellQt/Window>
#include <QDataStream>
#include <QEnterEvent>
#include <QFile>
#include <QGuiApplication>
#include <QImage>
#include <QKeyEvent>
#include <QElapsedTimer>
#include <QMouseEvent>
#include <QPainter>
#include <QSaveFile>
#include <QRasterWindow>
#include <QRegion>
#include <QScreen>
#include <QPointer>
#include <cmath>
#include <QSurfaceFormat>
#include <QTimer>
#include <QWheelEvent>
#include <QtEndian>
#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>

namespace {
constexpr int DemoWidth = 500, DemoHeight = 400;
constexpr QRect DemoRect(150, 100, 200, 200);
using namespace MateeFrame;

class PresenterWindow final : public QRasterWindow {
public:
    PresenterWindow(QString path, QString inputPath, bool heapAllocated=false)
        : path_(std::move(path)), inputPath_(std::move(inputPath)), heapAllocated_(heapAllocated) {
        setTitle(QStringLiteral("Matee Wayland Presenter"));
        QSurfaceFormat format; format.setAlphaBufferSize(8); setFormat(format);
        auto *layer=LayerShellQt::Window::get(this);
        layer->setLayer(LayerShellQt::Window::LayerOverlay);
        LayerShellQt::Window::Anchors anchors(LayerShellQt::Window::AnchorTop);
        anchors.setFlag(LayerShellQt::Window::AnchorLeft);
        layer->setAnchors(anchors);
        layer->setMargins(QMargins(0,0,80,80));
        layer->setExclusiveZone(-1);
        layer->setKeyboardInteractivity(LayerShellQt::Window::KeyboardInteractivityNone);
        layer->setScope(QStringLiteral("matee-wayland-presenter"));
        diagnostics_=qEnvironmentVariableIntValue("MATEENGINE_DRAG_DIAGNOSTICS")==1;
        if (diagnostics_) diagnosticTimer_.start();
        if (path_.isEmpty()) { resize(DemoWidth, DemoHeight); setMask(QRegion(DemoRect)); }
        else {
            resize(1, 1); setMask(QRegion()); maskTimer_.start();
            timer_.setInterval(8); connect(&timer_, &QTimer::timeout, this, [this] { poll(); }); timer_.start();
        }
        heartbeat_.setInterval(250);
        connect(&heartbeat_, &QTimer::timeout, this, [this] { publishInput(); });
        if (!inputPath_.isEmpty()) {
            heartbeat_.start();
            QTimer::singleShot(0, this, [this] { publishInput(); });
        }
    }
protected:
    void paintEvent(QPaintEvent *) override {
        QPainter p(this); p.setCompositionMode(QPainter::CompositionMode_Source);
        p.fillRect(QRect(QPoint(0, 0), size()), Qt::transparent);
        if (inputOnly_) return;
        if (!frame_.isNull()) {
            if (flipVertical_) { p.translate(0, height()); p.scale(1, -1); }
            p.drawImage(QRect(QPoint(0,0),size()), frame_); return;
        }
        p.setRenderHint(QPainter::Antialiasing); p.setBrush(QColor(30, 180, 255, 180));
        p.setPen(QPen(Qt::white, 4)); p.drawEllipse(DemoRect);
    }
    void mousePressEvent(QMouseEvent *e) override {
        updatePointer(e, true, false);
        std::fprintf(stderr, "Matee presenter pointer press at %.1f,%.1f\n", e->position().x(), e->position().y());
        std::fflush(stderr);
    }
    void mouseReleaseEvent(QMouseEvent *e) override {
        updatePointer(e, false, true);
        if (handoff_ && buttons_==0) finishHandoff(e->globalPosition());
    }
    void mouseMoveEvent(QMouseEvent *e) override { updatePointer(e, false, false); }
    bool event(QEvent *event) override {
        if (event->type()==QEvent::Enter) { inside_=true; pointer_=static_cast<QEnterEvent*>(event)->position(); publishInput(); }
        else if (event->type()==QEvent::Leave) { inside_=false; publishInput(); }
        return QRasterWindow::event(event);
    }
    void wheelEvent(QWheelEvent *e) override {
        pointer_=e->position();
        const QPoint delta=e->angleDelta().isNull()?e->pixelDelta()*3:e->angleDelta();
        scrollX_+=delta.x(); scrollY_+=delta.y(); publishInput();
    }
    void keyPressEvent(QKeyEvent *e) override {
        if (!keyboardRequested_) return;
        if (e->key()==Qt::Key_Backspace) appendKey(8);
        else if (e->key()==Qt::Key_Delete) appendKey(127);
        else if (e->key()==Qt::Key_Return || e->key()==Qt::Key_Enter) appendKey(13);
        else if (!(e->modifiers() & (Qt::ControlModifier|Qt::AltModifier|Qt::MetaModifier))) {
            const auto text=e->text().toUcs4();
            for (char32_t ch : text) if (ch>=32) appendKey(quint32(ch));
        }
    }
private:
    void finishHandoff(const QPointF &globalPointer) {
        handoff_->inputPath_=std::move(inputPath_);
        inputPath_.clear();
        handoff_->inputSequence_=inputSequence_;
        handoff_->buttons_=buttons_;
        handoff_->pointer_=handoff_->mapFromGlobal(globalPointer);
        for (int i=0;i<3;i++) { handoff_->down_[i]=down_[i]; handoff_->up_[i]=up_[i]; }
        handoff_->scrollX_=scrollX_; handoff_->scrollY_=scrollY_;
        handoff_->keyTotal_=keyTotal_; handoff_->keys_=keys_;
        handoff_->heartbeat_.start();
        handoff_->publishInput();
        if (!handoff_->isVisible()) handoff_->show();
        handoff_=nullptr;
        timer_.stop(); heartbeat_.stop(); hide();
        if (heapAllocated_) deleteLater();
        std::fprintf(stderr,"Matee presenter drag handoff completed after button release\n");
    }
    void appendKey(quint32 codepoint) { keys_[keyTotal_++%keys_.size()]=codepoint; publishInput(); }
    static int buttonIndex(Qt::MouseButton button) {
        if (button==Qt::LeftButton) return 0;
        if (button==Qt::RightButton) return 1;
        if (button==Qt::MiddleButton) return 2;
        return -1;
    }
    void updatePointer(QMouseEvent *e, bool pressed, bool released) {
        pointer_=e->position(); buttons_=quint32(e->buttons()); int index=buttonIndex(e->button());
        if (index>=0 && pressed) ++down_[index];
        if (index>=0 && released) ++up_[index];
        publishInput();
    }
    void publishInput() {
        if (inputPath_.isEmpty() || (!path_.isEmpty() && !inputOnly_ && (!frameAge_.isValid() || frameAge_.elapsed()>2000))) return;
        QSaveFile file(inputPath_); if (!file.open(QIODevice::WriteOnly)) return;
        QDataStream out(&file); out.setByteOrder(QDataStream::LittleEndian);
        out.writeRawData("MATEEINP",8); out << quint32(2) << qint32(pointer_.x()*frame_.width()/std::max(1,width())) << qint32(pointer_.y()*frame_.height()/std::max(1,height()))
            << buttons_ << quint32(inside_) << scrollX_ << scrollY_ << quint32(0) << ++inputSequence_
            << down_[0] << down_[1] << down_[2] << up_[0] << up_[1] << up_[2];
        out << keyTotal_; for (quint32 key : keys_) out << key;
        file.commit();
    }
    void poll() {
        if (frameAge_.isValid() && frameAge_.elapsed()>2000) { hide();  }
        QFile f(path_); if (!f.open(QIODevice::ReadOnly)) return;
        QByteArray h = f.read(HeaderSize);
        if (!validHeader(h)) return;
        quint32 w=u32(h,12), height=u32(h,16), stride=u32(h,20), format=u32(h,24), slotSize=u32(h,28), slot=u32(h,32);
        if (!w || !height || w>8192 || height>8192 || stride!=w*4 || slotSize!=stride*height || format!=1 || slot>1) return;
        quint64 seq=u64(h,40+slot*8); if (!seq || seq==sequence_) return;
        if (!f.seek(HeaderSize + static_cast<qint64>(slot)*slotSize)) return;
        QByteArray pixels=f.read(slotSize); if (pixels.size()!=slotSize || !f.seek(0)) return;
        QByteArray h2=f.read(HeaderSize); if (h2.size()!=HeaderSize || h2!=h) return;
        pixels_=std::move(pixels);
        frame_=QImage(reinterpret_cast<const uchar*>(pixels_.constData()), int(w), int(height), int(stride), QImage::Format_RGBA8888);
        sequence_=seq;
        const QRectF logical(f32(h,56),f32(h,60),f32(h,64),f32(h,68));
        if (!std::isfinite(logical.x()) || !std::isfinite(logical.y()) || !std::isfinite(logical.width()) || !std::isfinite(logical.height()) ||
            logical.width()<1 || logical.height()<1 || logical.width()>8192 || logical.height()>8192) return;
        const QString outputName=QString::fromUtf8(h.constData()+80,int(strnlen(h.constData()+80,128)));
        QScreen *output=nullptr;
        for (auto *candidate : QGuiApplication::screens()) if (candidate->name()==outputName) { output=candidate; break; }
        if (!output) { hide(); return; }
        const bool outputChanged=boundOutput_!=output;
        if (outputChanged && buttons_!=0 && !inputPath_.isEmpty()) {
            // Wayland sends a drag's release to the surface that received its
            // press. Keep that surface mapped while a new presenter displays
            // the pet on the destination output.
            handoff_=new PresenterWindow(path_,QString(),true);
            handoff_->poll();
            inputOnly_=true;
            timer_.stop();
            frameAge_.restart(); update();
            std::fprintf(stderr,"Matee presenter drag handoff waiting for button release\n");
            return;
        }
        if (outputChanged) {
            // wl_layer_surface output is immutable: recreate the role on migration.
            hide(); destroy();
            setGeometry(QRect(QPoint(qRound(logical.x()),qRound(logical.y())),QSize(qRound(logical.width()),qRound(logical.height()))));
            setScreen(output); boundOutput_=output;
        }
        const QSize logicalSize(qRound(logical.width()),qRound(logical.height()));
        const bool sizeChanged=size()!=logicalSize;
        if (sizeChanged) resize(logicalSize);
        auto *layer=LayerShellQt::Window::get(this);
        const QMargins margins(qRound(logical.x()-f32(h,208)),qRound(logical.y()-f32(h,212)),0,0);
        if (margins!=margins_ || outputChanged) { margins_=margins; layer->setMargins(margins_); if (diagnostics_) ++marginChanges_; }
        const quint32 flags=u32(h,72);
        const bool seated=(flags&8u)!=0;
        const bool topmost=(flags&1u)!=0;
        flipVertical_=(flags&2u)!=0;
        const bool keyboardRequested=(flags&4u)!=0;
        if (keyboardRequested!=keyboardRequested_) {
            keyboardRequested_=keyboardRequested;
            layer->setKeyboardInteractivity(keyboardRequested?LayerShellQt::Window::KeyboardInteractivityOnDemand:
                                    LayerShellQt::Window::KeyboardInteractivityNone);
        }
        if (topmost!=topmost_) {
            topmost_=topmost;
            // Layer-shell has no ordinary-window layer. Bottom permits normal
            // application windows to cover Matee; overlay implements topmost.
            layer->setLayer(topmost_?LayerShellQt::Window::LayerOverlay:LayerShellQt::Window::LayerBottom);
        }
        // Updating a large Wayland input region every animation frame is
        // expensive. Fifteen updates per second keeps hit testing responsive
        // while allowing presentation to run at a higher rate.
        if (sizeChanged || seated || outputChanged || maskTimer_.elapsed()>=66) { updateMask(); maskTimer_.restart(); }
        if (diagnostics_) {
            const qint64 now=diagnosticTimer_.elapsed();
            if (lastFrameAt_>0) maxFrameGapMs_=std::max(maxFrameGapMs_,now-lastFrameAt_);
            lastFrameAt_=now; ++diagnosticFrames_;
            if (now-lastReportAt_>=2000) {
                if (marginChanges_>0)
                    std::fprintf(stderr,"Matee drag presenter: frames %d, position changes %d, max frame gap %lld ms / %lld ms\n",
                                 diagnosticFrames_,marginChanges_,static_cast<long long>(maxFrameGapMs_),static_cast<long long>(now-lastReportAt_));
                lastReportAt_=now; diagnosticFrames_=marginChanges_=0; maxFrameGapMs_=0;
            }
        }
        frameAge_.restart();
        if (!isVisible()) { show(); }
        update();
    }
    void updateMask() {
        QRegion next=inputRegion(frame_,size(),flipVertical_);
        const bool transparent=next.isEmpty();
        if (transparent!=inputTransparent_) {
            inputTransparent_=transparent;
            setFlag(Qt::WindowTransparentForInput,inputTransparent_);
        }
        if (next!=mask_) {
            mask_=next;
            // An empty QWindow mask means "no mask" (the full window), not an
            // empty Wayland input region. Use Qt's explicit input-transparent
            // flag for a fully transparent frame.
            if (!mask_.isEmpty()) setMask(mask_);
        }
    }
    QPointer<QScreen> boundOutput_;
    QString path_, inputPath_; QTimer timer_, heartbeat_; QElapsedTimer maskTimer_, frameAge_; QByteArray pixels_; QImage frame_; QRegion mask_;
    quint64 sequence_=0, inputSequence_=0; QPointF pointer_; quint32 buttons_=0, down_[3]{}, up_[3]{};
    std::array<quint32,64> keys_{}; quint32 keyTotal_=0; bool keyboardRequested_=false;
    qint32 scrollX_=0, scrollY_=0; QMargins margins_; QElapsedTimer diagnosticTimer_;
    qint64 lastFrameAt_=0, lastReportAt_=0, maxFrameGapMs_=0; int diagnosticFrames_=0, marginChanges_=0;
    PresenterWindow *handoff_=nullptr;
    bool inside_=false, topmost_=true, flipVertical_=true, inputTransparent_=false, diagnostics_=false;
    bool inputOnly_=false, heapAllocated_=false;
};
}

int main(int argc, char **argv) {
    LayerShellQt::Shell::useLayerShell(); QGuiApplication app(argc,argv);
    QGuiApplication::setApplicationName(QStringLiteral("matee-wayland-presenter"));
    if (QGuiApplication::platformName()!=QStringLiteral("wayland")) {
        std::fprintf(stderr,"Matee presenter requires Qt Wayland; active platform is %s\n",qPrintable(QGuiApplication::platformName())); return 2; }
    QString path, inputPath; QStringList args=QGuiApplication::arguments();
    for (int i=1;i<args.size();++i) {
        if (args.at(i)==QStringLiteral("--frame-file") && i+1<args.size()) path=args.at(++i);
        else if (args.at(i)==QStringLiteral("--input-file") && i+1<args.size()) inputPath=args.at(++i);
        else { std::fprintf(stderr,"Usage: %s [--frame-file PATH] [--input-file PATH]\n",argv[0]); return 2; }
    }
    if (inputPath.isEmpty() && !path.isEmpty()) inputPath=path+QStringLiteral(".input");
    PresenterWindow window(path,inputPath); window.show(); return app.exec();
}
