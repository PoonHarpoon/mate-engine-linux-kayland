const fs=require('node:fs'), vm=require('node:vm'), assert=require('node:assert/strict');
function signal() { const listeners=[]; return {connect:f=>listeners.push(f),emit:(...args)=>listeners.forEach(f=>f(...args))}; }
const desktop={}, otherDesktop={};
const makeWindow=(id,overrides={})=>({internalId:id,pid:42,frameGeometry:{x:-1200.5,y:100.25,width:800.5,height:600},
    hidden:false,deleted:false,minimized:false,desktops:[desktop],activities:[],dock:false,desktopWindow:false,normalWindow:true,popupWindow:false,
    fullScreen:false,maximizeMode:0,opacity:1,resourceClass:'test',frameGeometryChanged:signal(),...overrides});
const target=makeWindow('target'), panel=makeWindow('panel',{dock:true,normalWindow:false}), popup=makeWindow('popup',{popupWindow:true,normalWindow:false});
const presenter=makeWindow('presenter',{resourceClass:'.matee-wayland-presenter-wrapped'});
const bundledPresenter=makeWindow('bundled-presenter',{resourceClass:'',pid:123});
const workspace={stackingOrder:[target,panel,popup,presenter,bundledPresenter],screens:[{name:'left',geometry:{x:-1280,y:0,width:1280,height:1024},devicePixelRatio:1.25}],
 currentDesktop:desktop,currentActivity:'work',cursorPos:{x:-30,y:120},windowAdded:signal(),windowRemoved:signal(),showingDesktop:false};
let pending, received=[], clock=1000;
const source=fs.readFileSync('Assets/MATE ENGINE - Scripts/APIs/Resources/MateeDesktopObserver.txt','utf8').replace('__BUS__','test').replace('__EPOCH__','epoch').replace('__PID__','999').replace('__PRESENTER_PID__','123');
vm.runInNewContext(source,{workspace,Date:{now:()=>clock},callDBus:(bus,path,iface,method,...args)=>{
 if(method==='DesktopSnapshot') received.push(JSON.parse(args[1]));
 assert.equal(pending,undefined,'only one callback may be outstanding'); pending=args.at(-1);
}});
function next(){ const callback=pending;pending=undefined;clock+=300;callback();return received.at(-1); }
assert.equal(received[0].windows[0].rect.x,-1200.5);assert.equal(received[0].outputs[0].scale,1.25);
assert.deepEqual(received[0].windows.map(w=>w.uuid),['target','panel','popup','presenter','bundled-presenter']);
assert.equal(received[0].windows[3].own,true,'wrapped presenter must never become a seat or occluder');
assert.equal(received[0].windows[4].own,true,'AppImage presenter with empty class must never become a seat or occluder');
assert.equal(received[0].windows[0].own,false,'other processes must remain foreign windows');
let callback=pending;pending=undefined;clock+=1;callback();
assert.equal(received.length,1,'unchanged state must not republish before heartbeat');
target.frameGeometry.x=-1199.75;target.frameGeometryChanged.emit();
callback=pending;pending=undefined;clock+=1;callback();
assert.equal(received.at(-1).windows[0].rect.x,-1199.75,'geometry signal must publish without heartbeat delay');
panel.hidden=true;assert.equal(next().windows[1].visible,false,'auto-hide');
target.minimized=true;assert.equal(next().windows[0].visible,false,'minimize');
target.minimized=false;target.desktops=[otherDesktop];assert.equal(next().windows[0].visible,false,'other desktop');
target.desktops=[];assert.equal(next().windows[0].visible,true,'all desktops');
target.activities=['elsewhere'];assert.equal(next().windows[0].visible,false,'other activity');
target.activities=[];workspace.showingDesktop=true;assert.equal(next().windows[0].visible,false,'show desktop');
workspace.showingDesktop=false;target.maximizeMode=3;assert.equal(next().windows[0].maximized,true);
workspace.stackingOrder=[popup,target];assert.deepEqual(next().windows.map(w=>w.uuid),['popup','target']);
target.maximizeMode=undefined;assert.equal(next().sittingSupported,false);
console.log('Desktop observer: geometry, order, removal, hidden panels, desktop/activity, maximize, capability checks passed');
