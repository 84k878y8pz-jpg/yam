# دليل نشر وتطبيق MetaCyber Agent على نظام macOS

هذا الدليل يشرح بالتفصيل كيفية بناء، نشر، وتطبيق MetaCyber Agent على أجهزة macOS. تم تصميم هذه النسخة لتتوافق مع بنية macOS (سواءً أجهزة Intel أو Apple Silicon) مع الحفاظ على جميع الخصائص الأمنية والإصلاحات الموجودة في نسخة Windows.

> **الإصدار الحالي: v3.5.4** — يتضمن إصلاح حرج لتعارض Mutex بين وضعي Service و UI الذي كان يمنع ظهور العلامة المائية والاتصال بالخادم.

---

## 1. المتطلبات الأساسية (Prerequisites)

قبل البدء في البناء والنشر، يجب التأكد من توفر المتطلبات التالية:

* **لبيئة التطوير والبناء (على جهاز المطور):**
  * نظام macOS (إصدار 11.0 Big Sur أو أحدث).
  * حزمة .NET 8.0 SDK مثبتة (يمكن تحميلها من [موقع Microsoft الرسمي](https://dotnet.microsoft.com/download)).
  * بيئة سطر الأوامر (Terminal).

* **لبيئة التشغيل (على أجهزة المستخدمين):**
  * نظام macOS (إصدار 11.0 Big Sur أو أحدث).
  * حزمة Python 3 مع مكتبة **tkinter** (مطلوبة لعرض العلامة المائية). للتحقق:
    ```bash
    python3 -c "import tkinter; print('tkinter OK')"
    ```
    إذا ظهر خطأ، قم بتثبيت tkinter عبر Homebrew:
    ```bash
    brew install python-tk
    ```
  * لا يلزم تثبيت .NET Runtime على أجهزة المستخدمين، حيث يتم بناء التطبيق كحزمة مستقلة (Self-Contained).

---

## 2. بنية التطبيق على macOS

يختلف أسلوب عمل الخدمات في macOS عن Windows. يعتمد MetaCyber Agent على نظام `launchd` بدلاً من Windows Services، ويتكون من ثلاثة أجزاء رئيسية:

1. **الخدمة الرئيسية (LaunchDaemon):**
   * تعمل بصلاحيات `root` في الخلفية منذ بدء تشغيل النظام.
   * تراقب تسجيل دخول وخروج المستخدمين.
   * المسار: `/Library/LaunchDaemons/com.metacyber.agent.plist`

2. **واجهة المستخدم والعلامة المائية (LaunchAgent / UI Worker):**
   * تعمل في سياق المستخدم الحالي (User Session).
   * تعرض العلامة المائية وتراقب عمليات التقاط الشاشة والطباعة.
   * المسار: `/Library/LaunchAgents/com.metacyber.agent.ui.plist`

3. **الحارس (Watchdog LaunchDaemon):**
   * يعمل بصلاحيات `root` لمراقبة الخدمة الرئيسية.
   * يرسل تنبيهات العبث (Tamper Notifications) في حال إيقاف الخدمة قسراً.
   * المسار: `/Library/LaunchDaemons/com.metacyber.watchdog.plist`

---

## 3. خطوات البناء (Build)

لقد قمنا بتجهيز سكريبت آلي (`build.sh`) لتسهيل عملية البناء وإنشاء الملفات التنفيذية المستقلة.

1. افتح تطبيق Terminal وانتقل إلى مجلد المشروع `MetaCyber_Agent_macOS`.
2. امنح السكريبت صلاحية التنفيذ:
   ```bash
   chmod +x scripts/build.sh
   ```
3. قم بتشغيل سكريبت البناء:
   ```bash
   ./scripts/build.sh
   ```
4. سيقوم السكريبت بتحديد معمارية جهازك تلقائياً (Apple Silicon `arm64` أو Intel `x64`) وإنشاء مجلد `publish/` يحتوي على جميع الملفات التنفيذية الجاهزة للنشر.

> **ملاحظة:** إذا كنت ترغب في البناء لمعمارية محددة، يمكنك استخدام متغير البيئة `TARGET_RID`، مثال: `TARGET_RID=osx-x64 ./scripts/build.sh`

---

## 4. إعداد ملف التكوين (Configuration)

قبل نشر التطبيق على أجهزة المستخدمين، يجب إعداد ملف `appsettings.json` الموجود في مجلد `publish/`.

1. افتح الملف باستخدام أي محرر نصوص:
   ```json
   {
     "AgentSettings": {
       "ServerUrl": "https://your-api-server.com",
       "ProductId": "YOUR_PRODUCT_ID"
     },
     "Logging": {
       "LogLevel": {
         "Default": "Information"
       }
     }
   }
   ```
2. استبدل `https://your-api-server.com` برابط خادم API الخاص بك.
3. استبدل `YOUR_PRODUCT_ID` بمعرف المنتج الصحيح.
4. احفظ الملف.

---

## 5. النشر والتثبيت (Deployment & Installation)

لنشر التطبيق على أجهزة المستخدمين، يمكنك توزيع مجلد `MetaCyber_Agent_macOS` بالكامل (بعد البناء وإعداد `appsettings.json`) واستخدام سكريبت التثبيت `install.sh`.

1. انقل المجلد إلى جهاز المستخدم المستهدف.
2. افتح Terminal وانتقل إلى المجلد.
3. امنح سكريبت التثبيت صلاحية التنفيذ:
   ```bash
   chmod +x scripts/install.sh
   ```
4. قم بتشغيل سكريبت التثبيت بصلاحيات المسؤول (root):
   ```bash
   sudo ./scripts/install.sh
   ```
5. سيقوم السكريبت بالخطوات التالية تلقائياً:
   * إنشاء المجلدات المطلوبة في `/Library/MetacyberAgent/`.
   * نسخ الملفات التنفيذية وسكريبت العلامة المائية.
   * ضبط الصلاحيات الأمنية الصحيحة (chown root:wheel).
   * تثبيت ملفات `plist` في `LaunchDaemons` و `LaunchAgents`.
   * تشغيل الخدمات فوراً عبر `launchctl`.

### التحقق من نجاح التثبيت
بعد انتهاء السكريبت، يمكنك التحقق من عمل الخدمات باستخدام الأوامر التالية:
```bash
sudo launchctl list | grep metacyber
```
يجب أن ترى `com.metacyber.agent` و `com.metacyber.watchdog` في القائمة.

---

## 6. مسارات الملفات والسجلات (Paths & Logs)

لمتابعة عمل الـ Agent أو استكشاف الأخطاء وإصلاحها، يمكنك مراجعة المسارات التالية:

* **مسار التثبيت الرئيسي:** `/Library/MetacyberAgent/`
* **ملف الإعدادات:** `/Library/MetacyberAgent/appsettings.json`
* **سجلات الخدمة الرئيسية (Service):** `/Library/Logs/MetacyberAgent/service-<date>.log`
* **سجلات الحارس (Watchdog):** `/Library/Logs/MetacyberAgent/watchdog-stdout.log`
* **سجلات واجهة المستخدم (UI):** `~/Library/Logs/MetacyberAgent/ui-<date>.log` (داخل مجلد المستخدم الحالي).
* **ملف إشارة الإيقاف الطبيعي:** `/Library/Application Support/MetacyberAgent/graceful_shutdown.flag`

---

## 7. إلغاء التثبيت (Uninstallation)

في حال الحاجة لإزالة الـ Agent بالكامل من الجهاز، استخدم سكريبت إلغاء التثبيت المرفق.

1. امنح السكريبت صلاحية التنفيذ:
   ```bash
   chmod +x scripts/uninstall.sh
   ```
2. قم بتشغيله بصلاحيات المسؤول:
   ```bash
   sudo ./scripts/uninstall.sh
   ```
3. سيقوم السكريبت بإيقاف جميع الخدمات عبر `launchctl` وحذف جميع ملفات التطبيق من النظام بشكل آمن، دون الحاجة لإعادة تشغيل الجهاز.

---

## 8. ملاحظات أمنية وقيود النظام (macOS Security Limitations)

نظراً للقيود الأمنية الصارمة في نظام macOS (مثل System Integrity Protection - SIP)، هناك بعض الاختلافات في كيفية تطبيق سياسات الحماية مقارنة بنظام Windows:

1. **منع التقاط الشاشة (Screen Capture Protection):**
   * لا يمكن منع أدوات التقاط الشاشة المدمجة في النظام بشكل كامل على مستوى النواة (Kernel) بدون تعريفات مخصصة (Kexts/System Extensions).
   * بدلاً من ذلك، تعتمد هذه النسخة على وضع العلامة المائية في طبقة عليا (`NSWindowLevel.screenSaver`) لتظهر فوق جميع النوافذ وتُدرج إجبارياً في أي لقطة شاشة.
   * يتم مراقبة مجلدات حفظ لقطات الشاشة الافتراضية وتسجيل الحدث وإرساله للخادم.

2. **العبث بالعمليات (Tamper Protection):**
   * الخدمة الرئيسية محمية بواسطة `launchd` الذي يعيد تشغيلها فوراً إذا توقفت.
   * يتم استخدام الـ Watchdog لإرسال إشعارات العبث في حال تم قتل العملية قسراً (kill -9).
   * تم تطبيق آلية (Deduplication) لمنع إرسال إشعارات عبث متكررة في غضون 10 ثوانٍ، مطابقة للإصلاح رقم 1 في الإصدار v3.5.3.

3. **رصد الطباعة (Print Monitoring):**
   * يعتمد رصد الطباعة على تحليل سجلات نظام `CUPS` (Common Unix Printing System) في المسار `/var/log/cups/access_log` واستخدام أداة `lpstat`.

---
*تم إعداد هذا الدليل بواسطة Manus AI لضمان انتقال سلس وآمن لـ MetaCyber Agent إلى بيئة macOS.*
