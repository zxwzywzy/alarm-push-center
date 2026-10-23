'use strict';
'require view';
'require rpc';
'require fs';
'require ui';

var serviceStatus = rpc.declare({ object: 'service', method: 'list', params: ['name'], expect: {} });

return view.extend({
    load: function() { return Promise.all([serviceStatus('alarm-push-center'), fs.exec('/etc/init.d/alarm-push-center', ['enabled'])]); },
    render: function(data) {
        var enabled = data[1].code === 0;
        data = data[0];
        var instances = (data['alarm-push-center'] || {}).instances || {};
        var running = Object.keys(instances).some(function(key) { return instances[key].running; });
        var host = window.location.hostname;
        var url = 'http://' + (host.indexOf(':') >= 0 ? '[' + host + ']' : host) + ':5080/';
        var header = E('div', { style: 'display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap;margin-bottom:16px' }, [
            E('div', {}, [E('h2', { style: 'margin:0 0 6px' }, '报警推送中心'),
                E('span', { style: 'color:' + (running ? '#12805c' : '#b45309') }, running ? '● 后台服务运行中' : '● 后台服务未运行，请检查系统日志')]),
            E('a', { class: 'cbi-button cbi-button-action', href: url, target: '_blank', rel: 'noopener noreferrer' }, '在新窗口打开')
        ]);
        var startup = E('input', { type: 'checkbox', 'aria-label': '插件开机启动' });
        startup.checked = enabled;
        var run = E('button', { class: 'cbi-button cbi-button-action', type: 'button' }, running ? '停止插件' : '启动插件');
        var controls = E('div', { style: 'display:flex;align-items:center;gap:18px;margin-bottom:18px' }, [
            run, E('label', {}, [startup, ' 开机启动插件'])
        ]);
        async function change(action, reload) {
            run.disabled = startup.disabled = true;
            try {
                var result = await fs.exec('/etc/init.d/alarm-push-center', [action]);
                if (result.code !== 0) throw new Error('操作失败，请检查系统日志。');
                if (reload) window.location.reload();
            } catch (error) { ui.addNotification(null, E('p', {}, error.message), 'error'); }
            finally {
                startup.checked = (await fs.exec('/etc/init.d/alarm-push-center', ['enabled'])).code === 0;
                run.disabled = startup.disabled = false;
            }
        }
        run.addEventListener('click', function() { change(running ? 'stop' : 'start', true); });
        startup.addEventListener('change', function() { change(startup.checked ? 'enable' : 'disable', false); });
        return E('div', {}, [header, controls,
            !running ? E('p', { class: 'alert-message notice' }, '插件已停止，点击“启动插件”打开配置台。配置仍会保留。') :
            window.location.protocol === 'https:'
                ? E('p', { class: 'alert-message notice' }, '当前 LuCI 使用 HTTPS。请点击“在新窗口打开”访问配置台。')
                : E('iframe', { src: url + '?embedded=1', title: '报警推送中心配置台', style: 'width:100%;height:calc(100vh - 220px);min-height:580px;border:1px solid #dbe7e0;border-radius:16px;background:#f3f5f3' }),
            E('p', { style: 'color:#64748b;font-size:12px' }, '插件开关控制网页服务；配置台内的开关控制报警采集。管理密码独立于 OpenWrt 的 root 密码。')
        ]);
    },
    handleSaveApply: null,
    handleSave: null,
    handleReset: null
});
