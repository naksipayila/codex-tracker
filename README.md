# OpenCode Proje Şablonu

Bu repository, OpenCode ile yeni projelerde kullanılabilecek kalıcı bağlam, görev takibi ve oturum hafızası yapısını sağlar.

## Kurulum

Bu dosyaları yeni projenin kök dizinine kopyala ve köşeli parantez içindeki alanları proje bilgileriyle doldur.

Öncelikle şu dosyaları doldur:

1. `docs/product-context.md`: Ürünün amacı, kullanıcıları ve kapsamı
2. `docs/tech-context.md`: Teknoloji, komutlar, ortam ve entegrasyonlar
3. `docs/architecture.md`: Bileşenler, veri akışı ve sınırlar
4. `docs/conventions.md`: Kod, test ve Git kuralları
5. `AGENTS.md`: Projeye özel kalıcı agent kuralları

## Oturum Akışı

- Yeni oturumda OpenCode, `AGENTS.md` ve `opencode.json` içindeki talimat dosyalarını yükler.
- Geliştirme sırasında geçici durumları `PROGRESS.md`, görevleri `TODO.md` içinde tut.
- Oturum sonunda `/wrap-up` komutunu çalıştır.
- Kalıcı mimari kararları `docs/decisions/` altında ADR olarak kaydet.

## Dosya Rolleri

- `AGENTS.md`: Agent davranış kuralları ve dosya haritası
- `PROGRESS.md`: Aktif bağlam, son doğrulamalar ve oturum günlüğü
- `TODO.md`: Küçük ve doğrulanabilir görevler
- `docs/`: Ürün, teknik ve mimari proje hafızası
- `.opencode/commands/`: Tekrarlanabilir OpenCode komutları

## Güvenlik

- Gizli bilgileri kaynak koda veya loglara ekleme.
- `.env` dosyalarını commit etme.
- `opencode.json` içindeki bash izinlerini kullanıcı onayı gerektirecek şekilde bırak.
- `git reset`, `git restore`, `git clean` ve dosya silme komutları otomatik çalıştırılamaz.

## Bakım

`PROGRESS.md` içindeki oturum günlüğü büyüdüğünde eski kayıtları `docs/history/` altına taşı. Güncel durum ve sonraki adımlar `PROGRESS.md` içinde kısa tutulmalıdır.
