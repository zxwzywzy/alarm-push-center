import Capacitor
import Security

@objc(SecureStorePlugin)
public class SecureStorePlugin: CAPPlugin, CAPBridgedPlugin {
    public let identifier = "SecureStorePlugin"
    public let jsName = "SecureStore"
    public let pluginMethods: [CAPPluginMethod] = [CAPPluginMethod(name: "set",returnType: CAPPluginReturnPromise), CAPPluginMethod(name: "get",returnType: CAPPluginReturnPromise), CAPPluginMethod(name: "remove",returnType: CAPPluginReturnPromise)]
    private func query(_ key:String) -> [String:Any] {
        return [kSecClass as String:kSecClassGenericPassword,kSecAttrService as String:"cn.zxzyzy.alarmhub",kSecAttrAccount as String:key]
    }
    @objc func set(_ call:CAPPluginCall) {
        guard let key=call.getString("key"),let value=call.getString("value") else {call.reject("凭据格式不正确");return}
        let base=query(key)
        let attrs:[String:Any]=[kSecValueData as String:Data(value.utf8),kSecAttrAccessible as String:kSecAttrAccessibleWhenUnlockedThisDeviceOnly]
        let update=SecItemUpdate(base as CFDictionary,attrs as CFDictionary)
        if update==errSecSuccess {call.resolve();return}
        guard update==errSecItemNotFound else {call.reject("无法安全保存授权");return}
        var create=base;attrs.forEach{create[$0.key]=$0.value}
        if SecItemAdd(create as CFDictionary,nil)==errSecSuccess{call.resolve()}else{call.reject("无法安全保存授权")}
    }
    @objc func get(_ call:CAPPluginCall) {
        guard let key=call.getString("key") else {call.reject("凭据名称缺失");return}
        var q=query(key);q[kSecReturnData as String]=true;q[kSecMatchLimit as String]=kSecMatchLimitOne
        var item:CFTypeRef?;let status=SecItemCopyMatching(q as CFDictionary,&item)
        if status==errSecItemNotFound{call.resolve(["value":NSNull()]);return}
        guard status==errSecSuccess,let data=item as? Data,let value=String(data:data,encoding:.utf8) else{call.reject("授权无法读取，请重新连接");return}
        call.resolve(["value":value])
    }
    @objc func remove(_ call:CAPPluginCall) {
        guard let key=call.getString("key") else{call.reject("凭据名称缺失");return}
        let status=SecItemDelete(query(key) as CFDictionary)
        if status==errSecSuccess||status==errSecItemNotFound{call.resolve()}else{call.reject("无法移除授权")}
    }
}

class AlarmHubBridgeViewController:CAPBridgeViewController {
    override func capacitorDidLoad() {bridge?.registerPluginInstance(SecureStorePlugin())}
}
