package cn.zxzyzy.alarmhub;

import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;
import androidx.core.content.FileProvider;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import java.io.*;
import java.net.*;
import java.security.MessageDigest;
import java.util.*;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.net.ssl.HttpsURLConnection;

@CapacitorPlugin(name = "AppUpdate")
public class AppUpdatePlugin extends Plugin {
    private final AtomicBoolean busy = new AtomicBoolean();
    @PluginMethod public void getInfo(PluginCall call) {
        try {
            PackageInfo info=getContext().getPackageManager().getPackageInfo(getContext().getPackageName(),0);
            JSObject value=new JSObject(); value.put("version",info.versionName); call.resolve(value);
        } catch(Exception error) { call.reject("无法读取应用版本"); }
    }
    @PluginMethod public void installUpdate(PluginCall call) {
        String version=call.getString("version",""),sha=call.getString("sha256",""),url=call.getString("url",""),api=call.getString("apiUrl","");
        int size=call.getInt("size",0);
        String repository="";
        for(String candidate:Arrays.asList("alarm-push-center","alarm-push-center-downloads"))if(url.equals("https://github.com/zxwzywzy/"+candidate+"/releases/download/mobile-v"+version+"/AlarmHub-"+version+"-android.apk"))repository=candidate;
        if(!version.matches("\\d+\\.\\d+\\.\\d+")||!sha.matches("[a-fA-F0-9]{64}")||repository.isEmpty()||size<1||size>300*1024*1024||!api.isEmpty()&&!api.matches("^https://api\\.github\\.com/repos/zxwzywzy/"+repository+"/releases/assets/[1-9]\\d*$")){call.reject("更新来源或校验信息无效");return;}
        if(!busy.compareAndSet(false,true)){call.reject("更新正在进行中");return;}
        new Thread(()->{
            try {
                File folder=new File(getContext().getCacheDir(),"updates");folder.mkdirs();
                File file=new File(folder,sha.toLowerCase(Locale.ROOT)+".apk");
                if(!verify(file,sha,size)){
                    File partial=new File(folder,sha+".partial");
                    try {
                        HttpsURLConnection connection=open(new URL(api.isEmpty()?url:api));
                        try(InputStream input=connection.getInputStream();OutputStream output=new FileOutputStream(partial)){
                            byte[] buffer=new byte[65536];int count;long total=0;
                            while((count=input.read(buffer))!=-1){total+=count;if(total>size)throw new IOException("下载超出允许大小");output.write(buffer,0,count);}
                        } finally {connection.disconnect();}
                        if(!verify(partial,sha,size))throw new IOException("安装包 SHA256 校验失败");
                        if(file.exists()&&!file.delete())throw new IOException("无法替换临时安装包");
                        if(!partial.renameTo(file))throw new IOException("无法保存安装包");
                    } finally {if(partial.exists())partial.delete();}
                }
                verifyIdentity(file,version);
                getActivity().runOnUiThread(()->{
                    try {
                        JSObject value=new JSObject();
                        if(Build.VERSION.SDK_INT>=26&&!getContext().getPackageManager().canRequestPackageInstalls()){
                            getActivity().startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:"+getContext().getPackageName())));
                            value.put("status","permission_required");
                        }else{
                            Uri uri=FileProvider.getUriForFile(getContext(),getContext().getPackageName()+".fileprovider",file);
                            Intent intent=new Intent(Intent.ACTION_VIEW).setDataAndType(uri,"application/vnd.android.package-archive");
                            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_ACTIVITY_NEW_TASK);
                            getActivity().startActivity(intent);value.put("status","installer_opened");
                        }
                        call.resolve(value);
                    }catch(Exception error){call.reject("无法打开系统安装页面，请检查安装权限");}
                    finally{busy.set(false);}
                });
            }catch(Exception error){busy.set(false);call.reject(error.getMessage()==null?"更新未完成，原应用保持可用":error.getMessage());}
        },"AlarmHub-update").start();
    }
    private static HttpsURLConnection open(URL url)throws Exception{
        for(int i=0;i<6;i++){
            if(!url.getProtocol().equals("https")||(url.getPort()!=-1&&url.getPort()!=443)||
                !Arrays.asList("github.com","api.github.com","release-assets.githubusercontent.com","objects.githubusercontent.com").contains(url.getHost()))throw new IOException("下载跳转地址不受信任");
            HttpsURLConnection c=(HttpsURLConnection)url.openConnection();c.setInstanceFollowRedirects(false);c.setConnectTimeout(20000);c.setReadTimeout(60000);c.setRequestProperty("User-Agent","AlarmHub-Updater/1.0");c.setRequestProperty("Accept","application/octet-stream");
            int status=c.getResponseCode();
            if(status>=300&&status<400){String location=c.getHeaderField("Location");c.disconnect();if(location==null)throw new IOException("下载跳转无效");url=new URL(url,location);continue;}
            if(status!=200){c.disconnect();throw new IOException("下载失败，请稍后重试");}return c;
        }throw new IOException("下载跳转过多");
    }
    private static boolean verify(File file,String sha,int size)throws Exception{
        if(!file.isFile()||file.length()!=size)return false;
        MessageDigest digest=MessageDigest.getInstance("SHA-256");
        try(InputStream input=new FileInputStream(file)){byte[] bytes=new byte[65536];int n;while((n=input.read(bytes))!=-1)digest.update(bytes,0,n);}
        StringBuilder hex=new StringBuilder();for(byte b:digest.digest())hex.append(String.format(Locale.ROOT,"%02x",b&255));return hex.toString().equalsIgnoreCase(sha);
    }
    private Set<String> signatures(PackageInfo info)throws Exception{
        Signature[] signatures=Build.VERSION.SDK_INT>=28?info.signingInfo.getApkContentsSigners():info.signatures;
        if(signatures==null||signatures.length==0)throw new IOException("安装包缺少签名");
        Set<String> result=new TreeSet<>();for(Signature s:signatures)result.add(Base64.getEncoder().encodeToString(s.toByteArray()));return result;
    }
    private void verifyIdentity(File file,String version)throws Exception{
        PackageManager pm=getContext().getPackageManager();int flags=Build.VERSION.SDK_INT>=28?PackageManager.GET_SIGNING_CERTIFICATES:PackageManager.GET_SIGNATURES;
        PackageInfo installed=pm.getPackageInfo(getContext().getPackageName(),flags),incoming=pm.getPackageArchiveInfo(file.getAbsolutePath(),flags);
        if(incoming==null||!getContext().getPackageName().equals(incoming.packageName)||!version.equals(incoming.versionName)||!signatures(installed).equals(signatures(incoming)))throw new IOException("应用标识或安装签名不一致，未执行更新");
        long current=Build.VERSION.SDK_INT>=28?installed.getLongVersionCode():installed.versionCode,next=Build.VERSION.SDK_INT>=28?incoming.getLongVersionCode():incoming.versionCode;
        if(next<=current)throw new IOException("安装包不是更高版本，未执行降级");
    }
}
