# نمونه کد .NET فینوپال

کتابخانه درگاه پرداخت فینوپال برای .NET 8. شناسه بسته NuGet برابر `FinoPal` است. مبلغ‌ها به **ریال** هستند. حداقل ۱۰۰۰ ریال.

## نصب

در ویژوال استودیو از Tools، گزینه NuGet Package Manager و سپس Manage NuGet Packages for Solution را باز کنید و `FinoPal` را جستجو کنید.

```bash
dotnet add package FinoPal
```

```xml
<PackageReference Include="FinoPal" Version="1.0.0" />
```

تا وقتی بسته روی [nuget.org](https://www.nuget.org/packages/FinoPal) منتشر نشده، پروژه `FinoPal.csproj` همین مخزن را به سولوشن اضافه کنید.

## پیکربندی

`merchantId` را از پنل کاربری بردارید. برای محیط آزمایشی `sandbox` را `true` بگذارید.

| حالت | آدرس |
|---|---|
| واقعی | `https://payment.finopal.ir` |
| سندباکس | `https://sandbox.finopal.ir` |

```csharp
using FinoPal;

using var finopal = new FinoPalClient("fino-xxxx-xxxx-xxxx-xxxx", sandbox: false);

var created = await finopal.RequestAsync(new PaymentRequest
{
    Amount = 10000,
    Description = "سفارش ۱۲۳۴۵",
    CallbackUrl = "https://shop.example/callback",
    OrderId = "ORD-12345",
});
```

خریدار را به `created.PaymentUrl` بفرستید. بازگشت درگاه `Authority` و `Status` دارد. فقط وقتی `FinoPalClient.ShouldVerify(status)` برابر true است (`Status=OK`) تأیید کنید. مبلغ باید همان مبلغ درخواست باشد. کد ۱۰۰ تازه تأییدشده و کد ۱۰۱ قبلاً تأییدشده است.

```csharp
if (FinoPalClient.ShouldVerify(status))
{
    var verified = await finopal.VerifyAsync(authority, amount);
}
```

## استعلام، ریورس و کارمزد

```csharp
var inquiry = await finopal.InquireAsync(authority);
await finopal.ReverseAsync(authority);
var fee = await finopal.CalculateFeeAsync(1_000_000);
```

`FeeType` برابر `Merchant` یعنی کارمزد روی پذیرنده است و `Payer` یعنی روی خریدار. اگر `VerifyAsync` با خطای شبکه برگشت، پرداخت تازه نسازید و اول استعلام بگیرید.
