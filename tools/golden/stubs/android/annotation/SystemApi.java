package android.annotation;
/** Заглушка скрытой аннотации AOSP (см. UnsupportedAppUsage). */
public @interface SystemApi { Client client() default Client.PRIVILEGED_APPS; enum Client { PRIVILEGED_APPS, MODULE_LIBRARIES } }
