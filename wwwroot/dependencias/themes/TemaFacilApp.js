(function(){'use strict';
const validos=['default','v1-cyberpunk','dark-tech','obsidian-gold','deep-teal','codex-dark','jotape'];
const chave='facilapp_tema_padrao';
function normalizar(tema){tema=String(tema||'').toLowerCase();return validos.includes(tema)?tema:'default'}
function obter(){try{return normalizar(localStorage.getItem(chave)||'jotape')}catch{return'jotape'}}
function aplicar(tema,gravar){tema=normalizar(tema||obter());document.documentElement.dataset.theme=tema;if(document.body)document.body.dataset.theme=tema;const seletor=document.getElementById('theme-select');if(seletor)seletor.value=tema;if(gravar!==false)try{localStorage.setItem(chave,tema)}catch{}document.querySelectorAll('iframe').forEach(frame=>{try{frame.contentWindow.postMessage({origem:'FACILAPP_TEMA_ALTERADO',tema},'*')}catch{}});window.dispatchEvent(new CustomEvent('facilapp:tema-alterado',{detail:{tema}}));return tema}
window.FacilAppTema={aplicar,obter,validos};
if(window.parent!==window){try{const pai=window.parent.document.documentElement.dataset.theme;if(pai)aplicar(pai,false);else aplicar(obter(),false)}catch{aplicar(obter(),false)}}else aplicar(obter(),false);
document.addEventListener('DOMContentLoaded',()=>aplicar(document.documentElement.dataset.theme||obter(),false));
window.addEventListener('storage',event=>{if(event.key===chave)aplicar(event.newValue,false)});
window.addEventListener('message',event=>{const dados=event.data||{};if(dados.origem==='FACILAPP_TEMA_ALTERADO')aplicar(dados.tema,false)});
}());
