plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// One version for both apps: CI passes the git tag (v1.4.0 -> -PappVersion=1.4.0). Local builds use the default.
val appVersion = (findProperty("appVersion") as String?) ?: "1.3.0"
val (vMajor, vMinor, vPatch) = appVersion.split('.').map { it.takeWhile(Char::isDigit).toInt() }
// "owner/repo" whose GitHub Releases the app checks for updates; empty = update check off (local builds).
val updateRepo = (findProperty("updateRepo") as String?) ?: ""
val keystore: String? = System.getenv("ANDROID_KEYSTORE")

android {
    namespace = "com.danet.booster"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.danet.booster"
        minSdk = 26
        targetSdk = 35
        versionCode = vMajor * 10000 + vMinor * 100 + vPatch
        versionName = appVersion
        buildConfigField("String", "UPDATE_REPO", "\"$updateRepo\"")
    }
    buildFeatures { buildConfig = true }

    signingConfigs {
        // Release key from CI secrets. It must never change, or installed apps refuse the update.
        if (keystore != null) create("release") {
            storeFile = file(keystore)
            storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
            keyAlias = System.getenv("ANDROID_KEY_ALIAS")
            keyPassword = System.getenv("ANDROID_KEY_PASSWORD")
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
}
