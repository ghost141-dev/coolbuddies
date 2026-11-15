const CONFIG={API:"",WHATSAPP:""}; // API: your API address, e.g. https://api.example.com   WHATSAPP: e.g. 27821234567
const P=[{id:1,n:"12000btu Midwall Split Unit",d:"Supply and installation",p:7000,i:"images/p12.jpg"},{id:2,n:"18000btu Midwall Split Unit",d:"Supply and installation",p:11400,i:"images/p18.jpg"},{id:3,n:"Car Aircon Regas",d:"Car air conditioner gas refill",p:450,i:"images/pcar.jpg"}];
const cart={},$=s=>document.querySelector(s),R=n=>"R "+n.toLocaleString("en-ZA");
$("#prods").innerHTML=P.map(p=>`<article class="c"><img src="${p.i}" alt="${p.n}" loading="lazy"><div class="pb"><h3>${p.n}</h3><span class="sub">${p.d}</span><span class="price">${R(p.p)}</span><button class="btn" type="button" data-id="${p.id}">Add to cart</button></div></article>`).join("");
const L=()=>Object.entries(cart).filter(([,q])=>q>0);
function draw(){const l=L(),c=l.reduce((a,[,q])=>a+q,0);$("#n").textContent=$("#n2").textContent=c;
$("#lines").innerHTML=l.length?l.map(([id,q])=>{const p=P.find(x=>x.id==id);return `<li><span>${p.n}</span><span><button type="button" data-a="-1" data-id="${id}" aria-label="Remove one">−</button> ${q} <button type="button" data-a="1" data-id="${id}" aria-label="Add one">+</button></span><b>${R(p.p*q)}</b></li>`}).join(""):"<li>Your cart is empty. Add a package to continue.</li>";
$("#tot").textContent=R(l.reduce((a,[id,q])=>a+P.find(x=>x.id==id).p*q,0));$("#pay").disabled=!l.length}
$("#prods").onclick=e=>{const b=e.target.closest("button");if(!b)return;cart[b.dataset.id]=Math.min((cart[b.dataset.id]||0)+1,10);draw();b.textContent="Added to cart";setTimeout(()=>b.textContent="Add to cart",1200)};
$("#lines").onclick=e=>{const b=e.target.closest("button");if(!b)return;cart[b.dataset.id]=Math.max(0,Math.min(10,cart[b.dataset.id]+ +b.dataset.a));draw()};
$("#open").onclick=$("#fab").onclick=()=>{$("#msg").textContent="";$("#co").showModal()};$("#x").onclick=()=>$("#co").close();
$("#f").onsubmit=async e=>{e.preventDefault();const m=$("#msg"),b=$("#pay");
if(!CONFIG.API){m.textContent="Demo mode: set CONFIG.API in the page script to take payments.";return}
b.disabled=true;m.textContent="";
try{const d=Object.fromEntries(new FormData(e.target));d.items=L().map(([id,q])=>({productId:+id,qty:q}));
const r=await fetch(CONFIG.API+"/api/orders",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(d)});
if(!r.ok)throw 0;const o=await r.json(),f=document.createElement("form");f.method="post";f.action=o.action;
for(const[k,v]of Object.entries(o.fields)){const i=document.createElement("input");i.type="hidden";i.name=k;i.value=v;f.append(i)}
document.body.append(f);f.submit()}catch{m.textContent="We could not start your payment. Check your details and try again, or contact us.";b.disabled=false}};
if(CONFIG.WHATSAPP){const a=$("#wa");a.href="https://wa.me/"+CONFIG.WHATSAPP;a.hidden=false}
const q=location.search;if(/paid=1/.test(q)||/cancelled=1/.test(q)){const b=$("#bn");b.style.display="block";b.textContent=/paid=1/.test(q)?"Thank you. Your payment is being confirmed and we will be in touch.":"Your payment was cancelled. Your cart is still available."}
draw();
