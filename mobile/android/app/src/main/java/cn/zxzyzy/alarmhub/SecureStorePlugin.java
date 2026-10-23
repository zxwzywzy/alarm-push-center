package cn.zxzyzy.alarmhub;

import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

@CapacitorPlugin(name = "SecureStore")
public class SecureStorePlugin extends Plugin {
    private static final String ALIAS = "AlarmHubCredentials";
    private SharedPreferences preferences() { return getContext().getSharedPreferences("alarmhub-secure", 0); }
    private SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if (store.containsAlias(ALIAS)) return ((KeyStore.SecretKeyEntry)store.getEntry(ALIAS,null)).getSecretKey();
        KeyGenerator gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");
        gen.init(new KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
        return gen.generateKey();
    }
    @PluginMethod public synchronized void set(PluginCall call) {
        String name=call.getString("key"),value=call.getString("value");
        if(name==null||value==null) {call.reject("凭据格式不正确");return;}
        try {
            Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,key());
            cipher.updateAAD(name.getBytes(StandardCharsets.UTF_8));
            String stored=Base64.encodeToString(cipher.getIV(),Base64.NO_WRAP)+":"+Base64.encodeToString(cipher.doFinal(value.getBytes(StandardCharsets.UTF_8)),Base64.NO_WRAP);
            if(!preferences().edit().putString(name,stored).commit())throw new Exception();
            call.resolve();
        } catch(Exception e) {call.reject("无法安全保存授权");}
    }
    @PluginMethod public synchronized void get(PluginCall call) {
        String name=call.getString("key");if(name==null){call.reject("凭据名称缺失");return;}
        try {
            String stored=preferences().getString(name,null);JSObject result=new JSObject();
            if(stored==null){result.put("value",JSObject.NULL);call.resolve(result);return;}
            String[] pieces=stored.split(":",2);Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE,key(),new GCMParameterSpec(128,Base64.decode(pieces[0],Base64.NO_WRAP)));
            cipher.updateAAD(name.getBytes(StandardCharsets.UTF_8));
            result.put("value",new String(cipher.doFinal(Base64.decode(pieces[1],Base64.NO_WRAP)),StandardCharsets.UTF_8));call.resolve(result);
        } catch(Exception e){call.reject("授权无法读取，请重新连接");}
    }
    @PluginMethod public synchronized void remove(PluginCall call) {
        String name=call.getString("key");if(name==null){call.reject("凭据名称缺失");return;}
        if(preferences().edit().remove(name).commit())call.resolve();else call.reject("无法移除授权");
    }
}
