package app.rotina

import android.app.admin.DeviceAdminReceiver
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent

/**
 * Device admin: while active, Android refuses to uninstall Rotina. Turning it off is done in Settings,
 * which is blocked during an activity. It uses no policy; it exists only for that uninstall guard.
 */
class AdminReceiver : DeviceAdminReceiver() {
    override fun onDisableRequested(context: Context, intent: Intent): CharSequence =
        "Sem esta proteção, o Rotina pode ser desinstalado e deixar de bloquear os apps."

    companion object {
        fun component(context: Context) = ComponentName(context, AdminReceiver::class.java)

        fun isActive(context: Context) =
            context.getSystemService(DevicePolicyManager::class.java).isAdminActive(component(context))

        fun requestIntent(context: Context): Intent = Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN)
            .putExtra(DevicePolicyManager.EXTRA_DEVICE_ADMIN, component(context))
            .putExtra(DevicePolicyManager.EXTRA_ADD_EXPLANATION,
                "Impede que o Rotina seja desinstalado. Para desligar, vá às Configurações fora de uma atividade.")
    }
}
