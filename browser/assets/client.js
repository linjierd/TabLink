"use strict";
(()=>{
 const $=id=>document.getElementById(id),video=$("screen");
 let token=new URLSearchParams(location.hash.slice(1)).get("token")||"";
 history.replaceState(null,"",location.pathname);
 let peer,ws,control,closed=false,paused=true,offered=false,ice=[],profile,pointer=null,lastPoint={x:.5,y:.5},lastMove=0,presented=0,lastReport=0,wake;
 const status=text=>{$("status").textContent=text;$("liveStatus").textContent=text;};
 const signal=value=>{if(ws?.readyState===WebSocket.OPEN)ws.send(JSON.stringify(value));};
 const send=value=>{if(control?.readyState==="open"&&control.bufferedAmount<16384)control.send(JSON.stringify(value));};
 function release(){if(pointer!==null){send({type:"input",kind:"up",...lastPoint});pointer=null;}}
 function stop(message){if(closed)return;closed=true;release();paused=true;try{control?.close();peer?.close();ws?.close();wake?.release();}catch{}video.srcObject=null;$("viewer").hidden=true;$("setup").hidden=false;status(message);$("start").disabled=true;}
 function position(event){const r=video.getBoundingClientRect(),ratio=profile.width/profile.height;let w=r.width,h=w/ratio;if(h>r.height){h=r.height;w=h*ratio;}const x=(event.clientX-r.left-(r.width-w)/2)/w,y=(event.clientY-r.top-(r.height-h)/2)/h;return{x:Math.max(0,Math.min(1,x)),y:Math.max(0,Math.min(1,y)),inside:x>=0&&x<=1&&y>=0&&y<=1};}
 video.addEventListener("pointerdown",event=>{if(paused||pointer!==null||document.hidden)return;const p=position(event);if(!p.inside)return;event.preventDefault();pointer=event.pointerId;lastPoint={x:p.x,y:p.y};video.setPointerCapture(pointer);send({type:"input",kind:"down",...lastPoint});});
 video.addEventListener("pointermove",event=>{if(event.pointerId!==pointer||paused)return;const p=position(event);lastPoint={x:p.x,y:p.y};if(performance.now()-lastMove>=16){lastMove=performance.now();send({type:"input",kind:"move",...lastPoint});}});
 for(const name of ["pointerup","pointercancel","lostpointercapture"])video.addEventListener(name,event=>{if(event.pointerId===pointer)release();});
 video.addEventListener("wheel",event=>{if(paused)return;const p=position(event);if(!p.inside)return;event.preventDefault();send({type:"input",kind:"scroll",x:p.x,y:p.y,delta:Math.max(-1200,Math.min(1200,-event.deltaY))});},{passive:false});
 document.addEventListener("visibilitychange",()=>{if(document.hidden)release();});
 window.addEventListener("pagehide",()=>stop("连接已结束。请在电脑端生成新的配对二维码。"));
 $("disconnect").onclick=()=>stop("已断开副屏。再次连接请扫描新的配对二维码。");
 $("fullscreen").onclick=async()=>{try{if(document.fullscreenElement)await document.exitFullscreen();else if($("viewer").requestFullscreen)await $("viewer").requestFullscreen();else status("当前浏览器不支持页面全屏，可使用页面模式。");}catch{status("当前系统未允许全屏，画面继续在页面内显示。");}};
 if(!/^[a-f0-9]{64}$/.test(token)){status("二维码缺失或无效。请在电脑端生成新的浏览器配对二维码。");$("start").disabled=true;}
 if(!window.isSecureContext||!window.RTCPeerConnection||!video.requestVideoFrameCallback){status("此浏览器不支持安全副屏或实际呈现回报。请更新浏览器，并信任电脑导出的 CA 证书。");$("start").disabled=true;}
 $("orientation").value=innerWidth>=innerHeight?"landscape":"portrait";
 $("start").onclick=async()=>{
  $("start").disabled=true;status("正在建立安全连接…");
  const short=Number($("quality").value),long=short===720?1280:1920,fps=Number($("fps").value),portrait=$("orientation").value==="portrait",width=portrait?short:long,height=portrait?long:short;
  profile={width,height,rotation:portrait?0:1,activeModeId:1,refreshRate:fps,nativeWidth:width,nativeHeight:height,supportedModes:[{width,height,refreshRate:fps,modeId:1}]};
  try{
   peer=new RTCPeerConnection({iceServers:[],bundlePolicy:"max-bundle",rtcpMuxPolicy:"require"});
   const transceiver=peer.addTransceiver("video",{direction:"recvonly"});
   const codecs=RTCRtpReceiver.getCapabilities?.("video")?.codecs?.filter(c=>c.mimeType.toLowerCase()==="video/h264"&&/packetization-mode=1/.test(c.sdpFmtpLine||"")&&/profile-level-id=42[0-9a-f]{4}/i.test(c.sdpFmtpLine||""));
   if(transceiver.setCodecPreferences&&codecs?.length)transceiver.setCodecPreferences(codecs);
   control=peer.createDataChannel("control",{ordered:true});
   control.onclose=()=>stop("触控通道已断开。请重新配对。");
   peer.onicecandidate=event=>{if(event.candidate){const value={type:"ice",candidate:event.candidate.toJSON()};if(offered)signal(value);else ice.push(value);}};
   peer.onconnectionstatechange=()=>{if(peer.connectionState==="failed"||peer.connectionState==="closed")stop("网络连接已结束。请重新配对。");};
   peer.ontrack=async event=>{video.muted=true;video.srcObject=event.streams[0]||new MediaStream([event.track]);$("setup").hidden=true;$("viewer").hidden=false;try{await video.play();}catch{if(!closed){status("请轻触画面开始播放。");video.onclick=()=>video.play();}}if(!closed&&navigator.wakeLock)try{wake=await navigator.wakeLock.request("screen");}catch{}};
   ws=new WebSocket(`wss://${location.host}/signal`);
   ws.onclose=()=>stop("连接已结束。请在电脑端生成新的配对二维码。");
   ws.onerror=()=>stop("安全连接失败。请检查本地网络和 CA 证书信任。");
   ws.onmessage=async event=>{try{const message=JSON.parse(event.data);if(message.type==="answer"){await peer.setRemoteDescription({type:"answer",sdp:message.sdp});for(const item of remoteIce)await peer.addIceCandidate(item);remoteIce=[];}else if(message.type==="ice"){if(peer.remoteDescription)await peer.addIceCandidate(message.candidate);else remoteIce.push(message.candidate);}else if(message.type==="status"){status(message.message);paused=message.capturePaused||message.state!=="streaming";$("paused").hidden=!message.capturePaused;if(paused)release();}else if(message.type==="error")stop(message.message);}catch{stop("浏览器无法完成视频协商，请检查浏览器是否支持 H.264。");}};
   let remoteIce=[];
   ws.onopen=async()=>{try{signal({type:"hello",token,profile});token="";const offer=await peer.createOffer();await peer.setLocalDescription(offer);signal({type:"offer",sdp:offer.sdp});offered=true;for(const candidate of ice)signal(candidate);ice=[];}catch{stop("无法创建浏览器视频连接。");}};
   const frame=(now,metadata)=>{if(closed)return;presented++;if(now-lastReport>=250&&metadata.width===profile.width&&metadata.height===profile.height){lastReport=now;send({type:"presented",frames:presented,width:metadata.width,height:metadata.height});}video.requestVideoFrameCallback(frame);};
   video.requestVideoFrameCallback(frame);
  }catch{stop("此浏览器不支持所需的视频功能。请更新浏览器后重新配对。");}
 };
})();
