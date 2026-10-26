import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

const xml = `<node><interface name="io.github.zoefix.AgentSpeed1">
<method name="GetSnapshot"><arg type="s" direction="out"/></method>
<method name="Activate"><arg type="s" direction="in"/></method>
</interface></node>`;
const Proxy = Gio.DBusProxy.makeProxyWrapper(xml);

export default class AgentSpeed extends Extension {
    enable() {
        this._generation = (this._generation ?? 0) + 1;
        const generation = this._generation;
        this._button = new PanelMenu.Button(0.0, 'Agent Speed');
        this._label = new St.Label({text: '— tok/s', style_class: 'agent-speed-value', y_align: 2});
        this._button.add_child(this._label);
        this._details = new PopupMenu.PopupMenuItem('Agent Speed', {reactive: false});
        this._button.menu.addMenuItem(this._details);
        const language = GLib.get_language_names()[0].toLowerCase();
        const column = language.startsWith('ja') ? 3 : language.startsWith('zh') ? (/tw|hk|mo|hant/.test(language) ? 2 : 1) : 0;
        for (const labels of [
            ['Open panel', '打开面板', '開啟面板', 'パネルを開く', 'dashboard'],
            ['Usage history', '用量历史', '用量歷史', '使用履歴', 'history'],
            ['Settings', '设置', '設定', '設定', 'settings'],
            ['Show / hide orbs', '显示／隐藏悬浮窗', '顯示／隱藏懸浮窗', 'フロート表示切替', 'toggle-orbs'],
            ['Restore mouse input', '恢复鼠标交互', '恢復滑鼠操作', 'マウス操作を復元', 'restore-input'],
            ['Quit', '退出', '結束', '終了', 'quit'],
        ]) {
            const label = labels[column], action = labels[4];
            const item = new PopupMenu.PopupMenuItem(label);
            item.connect('activate', () => {
                if (this._proxy?.g_name_owner) this._proxy.ActivateRemote(action, () => {});
                else if (action !== 'quit') Gio.DesktopAppInfo.new('agent-speed.desktop')?.launch([], null);
            });
            this._button.menu.addMenuItem(item);
        }
        Main.panel.addToStatusArea(this.uuid, this._button);
        this._cancel = new Gio.Cancellable();
        this._proxy = new Proxy(Gio.DBus.session, 'io.github.zoefix.AgentSpeed', '/io/github/zoefix/AgentSpeed',
            (_proxy, error) => { if (!error && this._generation === generation) this._poll(); }, this._cancel);
        this._timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 500, () => { this._poll(); return GLib.SOURCE_CONTINUE; });
    }
    _poll() {
        if (!this._proxy?.g_name_owner || this._pending || !this._button) {
            if (!this._proxy?.g_name_owner && this._label) this._label.text = '— tok/s';
            return;
        }
        const generation = this._generation;
        this._pending = true;
        this._proxy.GetSnapshotRemote((result, error) => {
            if (this._generation !== generation || !this._button) return;
            this._pending = false;
            if (error) return;
            try {
                const value = JSON.parse(result[0]);
                if (value.schema !== 1 || !Number.isFinite(value.speed)) return;
                this._label.text = typeof value.label === 'string' && value.label.length <= 100 ? value.label : `${value.speed.toFixed(1)} tok/s`;
                const rate = n => {
                    if (n === null) return '—';
                    const amount = n * (value.bits ? 8 : 1), unit = value.bits ? 'b/s' : 'B/s';
                    return amount >= 1000000 ? `${(amount / 1000000).toFixed(1)} M${unit}` : amount >= 1000 ? `${(amount / 1000).toFixed(1)} K${unit}` : `${amount.toFixed(0)} ${unit}`;
                };
                this._details.label.text = `${value.agents} agents · ${value.cost}\n↓ ${rate(value.rx)}  ↑ ${rate(value.tx)}`;
            } catch (_) { /* A mismatched app cannot disrupt GNOME Shell. */ }
        }, this._cancel);
    }
    disable() {
        this._generation++;
        this._cancel?.cancel(); this._cancel = null;
        if (this._timer) { GLib.source_remove(this._timer); this._timer = null; }
        this._proxy = null; this._pending = false;
        this._button?.destroy(); this._button = null; this._label = null; this._details = null;
    }
}
