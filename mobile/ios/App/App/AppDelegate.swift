import UIKit
import Capacitor

@UIApplicationMain
class AppDelegate: UIResponder, UIApplicationDelegate {

    var window: UIWindow?

    func application(_ application: UIApplication, didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?) -> Bool {
        // Override point for customization after application launch.
        return true
    }

    func applicationWillResignActive(_ application: UIApplication) {
        // Sent when the application is about to move from active to inactive state. This can occur for certain types of temporary interruptions (such as an incoming phone call or SMS message) or when the user quits the application and it begins the transition to the background state.
        // Use this method to pause ongoing tasks, disable timers, and invalidate graphics rendering callbacks. Games should use this method to pause the game.
    }

    func applicationDidEnterBackground(_ application: UIApplication) {
        // Use this method to release shared resources, save user data, invalidate timers, and store enough application state information to restore your application to its current state in case it is terminated later.
        // If your application supports background execution, this method is called instead of applicationWillTerminate: when the user quits.
    }

    func applicationWillEnterForeground(_ application: UIApplication) {
        // Called as part of the transition from the background to the active state; here you can undo many of the changes made on entering the background.
    }

    func applicationDidBecomeActive(_ application: UIApplication) {
        // Restart any tasks that were paused (or not yet started) while the application was inactive. If the application was previously in the background, optionally refresh the user interface.
    }

    func applicationWillTerminate(_ application: UIApplication) {
        // Called when the application is about to terminate. Save data if appropriate. See also applicationDidEnterBackground:.
    }

    func application(_ application: UIApplication,
                     configurationForConnecting connectingSceneSession: UISceneSession,
                     options: UIScene.ConnectionOptions) -> UISceneConfiguration {
        let config = UISceneConfiguration(name: "Default Configuration",
                                          sessionRole: connectingSceneSession.role)
        config.delegateClass = SceneDelegate.self
        return config
    }
}
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
    override func capacitorDidLoad() {
        bridge?.registerPluginInstance(SecureStorePlugin())
        bridge?.registerPluginInstance(AppUpdatePlugin())
    }
}

import CryptoKit

@objc(AppUpdatePlugin)
public class AppUpdatePlugin: CAPPlugin, CAPBridgedPlugin, URLSessionTaskDelegate {
    public let identifier = "AppUpdatePlugin"
    public let jsName = "AppUpdate"
    public let pluginMethods: [CAPPluginMethod] = [CAPPluginMethod(name:"getInfo",returnType:CAPPluginReturnPromise),CAPPluginMethod(name:"installUpdate",returnType:CAPPluginReturnPromise)]
    private var busy = false
    @objc func getInfo(_ call: CAPPluginCall) {
        call.resolve(["version":Bundle.main.object(forInfoDictionaryKey:"CFBundleShortVersionString") as? String ?? "0.0.0"])
    }
    @objc func installUpdate(_ call: CAPPluginCall) {
        guard let version=call.getString("version"),version.range(of:"^\\d+\\.\\d+\\.\\d+$",options:.regularExpression) != nil,
              let sha=call.getString("sha256"),sha.range(of:"^[a-fA-F0-9]{64}$",options:.regularExpression) != nil,
              let size=call.getInt("size"),size>0,size<=300*1024*1024,
              let address=call.getString("url"),let repository=["alarm-push-center","alarm-push-center-downloads"].first(where:{address=="https://github.com/zxwzywzy/\($0)/releases/download/mobile-v\(version)/AlarmHub-\(version)-unsigned.ipa"})
        else {call.reject("更新来源或校验信息无效");return}
        let api=call.getString("apiUrl") ?? ""
        guard api.isEmpty || api.range(of:"^https://api\\.github\\.com/repos/zxwzywzy/\(repository)/releases/assets/[1-9]\\d*$",options:.regularExpression) != nil,
              let url=URL(string:api.isEmpty ? address : api) else {call.reject("更新资产来源无效");return}
        guard !busy else {call.reject("更新正在进行中");return};busy=true
        let configuration=URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest=60;configuration.timeoutIntervalForResource=900
        let session=URLSession(configuration:configuration,delegate:self,delegateQueue:nil)
        var downloadRequest=URLRequest(url:url)
        downloadRequest.setValue("application/octet-stream",forHTTPHeaderField:"Accept")
        downloadRequest.setValue("AlarmHub-Updater/1.0",forHTTPHeaderField:"User-Agent")
        session.downloadTask(with:downloadRequest) { [weak self] temporary,response,error in
            defer {session.finishTasksAndInvalidate()}
            guard let self=self else {return}
            do {
                guard error==nil,let temporary=temporary,let response=response as? HTTPURLResponse,response.statusCode==200 else {throw NSError(domain:"更新下载失败，请稍后重试",code:1)}
                let attributes=try FileManager.default.attributesOfItem(atPath:temporary.path)
                guard (attributes[.size] as? NSNumber)?.intValue==size else {throw NSError(domain:"更新文件长度不符",code:1)}
                let input=try FileHandle(forReadingFrom:temporary);defer {try? input.close()}
                var digest=SHA256()
                while let data=try input.read(upToCount:65536),!data.isEmpty {digest.update(data:data)}
                let hash=digest.finalize().map{String(format:"%02x",$0)}.joined()
                guard hash==sha.lowercased() else {throw NSError(domain:"更新文件 SHA256 校验失败",code:1)}
                let destination=FileManager.default.temporaryDirectory.appendingPathComponent("AlarmHub-\(version)-unsigned.ipa")
                if FileManager.default.fileExists(atPath:destination.path){try FileManager.default.removeItem(at:destination)}
                try FileManager.default.moveItem(at:temporary,to:destination)
                DispatchQueue.main.async {
                    self.busy=false
                    guard let view=self.bridge?.viewController else {call.reject("无法打开文件分享");return}
                    let share=UIActivityViewController(activityItems:[destination],applicationActivities:nil)
                    share.popoverPresentationController?.sourceView=view.view
                    share.popoverPresentationController?.sourceRect=CGRect(x:view.view.bounds.midX,y:view.view.bounds.midY,width:1,height:1)
                    view.present(share,animated:true);call.resolve(["status":"downloaded"])
                }
            }catch{
                DispatchQueue.main.async {self.busy=false;call.reject("更新未完成，原应用保持可用。请检查网络或安装包校验。")}
            }
        }.resume()
    }
    public func urlSession(_ session: URLSession,task:URLSessionTask,willPerformHTTPRedirection response:HTTPURLResponse,newRequest request:URLRequest,completionHandler:@escaping(URLRequest?)->Void) {
        guard let url=request.url,url.scheme=="https",url.port==nil || url.port==443,
              ["github.com","api.github.com","release-assets.githubusercontent.com","objects.githubusercontent.com"].contains(url.host ?? "") else {completionHandler(nil);return}
        completionHandler(request)
    }
}
