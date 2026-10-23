(() => {
  const form = document.getElementById('update-settings-form');
  const status = document.getElementById('update-status');
  const apply = document.getElementById('update-apply');
  const check = document.getElementById('update-check');
  let view, busy = false, loaded = false;
  enhanceSecrets(form);
  function render(data, preferences = false) {
    view = data;
    document.getElementById('update-current').textContent = `当前版本 ${data.status.current_version}`;
    status.textContent = data.operation?.message || data.status.message;
    if (data.status.available) status.textContent = data.status.message;
    apply.disabled = !data.supported || !data.status.available || data.applying || busy;
    if (data.status.release?.notes_url) document.getElementById('update-notes').href = data.status.release.notes_url;
    if (preferences) for (const [key, value] of Object.entries(data.preferences)) {
      const input = form.elements[key]; if (!input) continue;
      if (input.type === 'checkbox') input.checked = value; else input.value = value;
    }
  }
  async function load() {
    try { render(await api('/api/updates'), true); loaded = true; }
    catch (error) { status.textContent = error.message; }
  }
  check.onclick = async () => {
    if (busy) return; busy = true; check.disabled = true; status.textContent = '正在检查 GitHub 正式版本…';
    try { render(await api('/api/updates/check', {method:'POST',signal:AbortSignal.timeout(30000)})); }
    catch(error) { status.textContent = error.message; }
    finally { busy = false; check.disabled = false; if(view) apply.disabled = !view.supported || !view.status.available || view.applying; }
  };
  form.onsubmit = async event => {
    event.preventDefault();
    const fields = new FormData(form), button = form.querySelector('[type=submit]'); button.disabled = true;
    try {
      render(await api('/api/updates/settings', {method:'PUT', body:JSON.stringify({current_password:fields.get('current_password'),preferences:{
        check_automatically:form.elements.check_automatically.checked,install_automatically:form.elements.install_automatically.checked,
        maintenance_start_hour:Number(fields.get('maintenance_start_hour')),maintenance_end_hour:Number(fields.get('maintenance_end_hour'))
      }})}),true);
      form.elements.current_password.value = ''; status.textContent = '更新偏好已保存。';
    } catch(error) { status.textContent = error.message; } finally { button.disabled = false; }
  };
  apply.onclick = () => {
    if(!view?.status.available || busy) return;
    if(isDirty()) { status.textContent = '页面还有未保存的修改，请先保存并应用，再更新。'; return; }
    if(!form.elements.current_password.reportValidity()) return;
    ask(`更新到 ${view.status.release.version}？`, '将先备份配置，核对安装包，再短暂重启插件。设备设置、手机授权和采集状态会保留。', async () => {
      busy = true; apply.disabled = true;
      try {
        const result = await api('/api/updates/apply', {method:'POST',signal:AbortSignal.timeout(15*60*1000),body:JSON.stringify({version:view.status.release.version,current_password:form.elements.current_password.value})});
        form.elements.current_password.value = ''; status.textContent = result.message;
        const poll = setInterval(async () => {
          try {
            const health = await (await fetch('/api/health',{cache:'no-store'})).json();
            if(health.version===view.status.release.version) { clearInterval(poll); location.reload(); }
          } catch { }
        },3000);
        setTimeout(()=>{ clearInterval(poll); busy=false;status.textContent='请刷新页面查看更新结果；若更新失败，可重新登录查看回退状态。'; },180000);
      } catch(error) { status.textContent=error.message; busy=false;apply.disabled=false; }
    });
  };
  document.addEventListener('click', event => { if(event.target.closest('[data-settings=updates]')) load(); });
  new MutationObserver(()=>{ if(!document.getElementById('app').classList.contains('hidden')&&!loaded)load(); }).observe(document.getElementById('app'),{attributes:true,attributeFilter:['class']});
  if(!document.getElementById('app').classList.contains('hidden'))load();
})();
