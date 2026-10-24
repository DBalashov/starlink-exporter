# Starlink Prometheus exporter

Опрашивает gRPC API тарелки Starlink (`SpaceX.API.Device.Device/Handle` на `192.168.100.1:9200`)
раз в 3 секунды и отдаёт метрики в формате Prometheus на `http://<host>:9817/metrics`.

## Как это устроено

При старте экспортер скачивает protobuf-схему с самой тарелки через gRPC server reflection,
кодирует запрос (`{"get_status": {}}`) и декодирует ответ по этой схеме. Поэтому после
обновления прошивки новые поля появляются в метриках автоматически.

Запасная схема — `.proto` из [clarkzjw/starlink-grpc-golang](https://github.com/clarkzjw/starlink-grpc-golang/tree/master/proto)
в папке `Protos/` (прошивка 2026.09.24.mr87636.5, лицензия репозитория GPL-3.0). Она
вкомпилирована в программу и используется, если тарелка отвечает, но reflection на ней
выключен (`Unimplemented`), или если задано `SchemaSource=Embedded`. Чтобы обновить, скопируйте
свежие файлы из `proto/spacex_api` того репозитория в `Protos/spacex_api` и пересоберите.

Правила преобразования ответа в метрики:

| Поле protobuf                  | Метрика                                                        |
|--------------------------------|----------------------------------------------------------------|
| число / bool                   | `starlink_dish_status_<путь>` (отсутствующее поле proto3 = 0)  |
| enum                           | числовое значение + имя в метке `..._info`                     |
| string                         | метки метрики `<путь>_info{...} 1`                             |
| вложенное сообщение            | рекурсивно, префикс `<путь>_<поле>`                            |
| repeated                       | метка `index` (длиннее `MaxRepeatedElements` — пропускается)   |
| deprecated, bytes              | не экспортируются                                              |

Служебные метрики: `starlink_up{request}`, `starlink_request_duration_seconds{request}`,
`starlink_last_poll_timestamp_seconds`.

Полезные метрики `get_status`: `starlink_dish_status_pop_ping_latency_ms`,
`starlink_dish_status_pop_ping_drop_rate`, `starlink_dish_status_downlink_throughput_bps`,
`starlink_dish_status_uplink_throughput_bps`, `starlink_dish_status_obstruction_stats_fraction_obstructed`,
`starlink_dish_status_device_state_uptime_s`, `starlink_dish_status_alerts_*`,
`starlink_dish_status_device_info_info{software_version=...}`.

## Запуск

```bash
dotnet run -c Release
```

## Настройка (`appsettings.json` или переменные окружения)

| Ключ                            | По умолчанию                 | Env                                    |
|---------------------------------|------------------------------|----------------------------------------|
| `Urls`                          | `http://0.0.0.0:9817`        | `URLS`                                 |
| `Starlink:Address`              | `http://192.168.100.1:9200`  | `Starlink__Address`                    |
| `Starlink:SchemaSource`         | `Auto`                       | `Starlink__SchemaSource`               |
| `Starlink:PollInterval`         | `00:00:03`                   | `Starlink__PollInterval`               |
| `Starlink:RequestTimeout`       | `00:00:02`                   | `Starlink__RequestTimeout`             |
| `Starlink:Requests`             | `["get_status"]`             | `Starlink__Requests__0`, `__1`, ...    |
| `Starlink:MaxRepeatedElements`  | `32`                         | `Starlink__MaxRepeatedElements`        |

`SchemaSource`: `Auto` — reflection, а при его отсутствии встроенные proto; `Reflection` — только
reflection; `Embedded` — только встроенные proto (без обращений к reflection).

`Requests` — имена полей сообщения `SpaceX.API.Device.Request`; каждый запрос экспортируется
с префиксом `starlink_<поле ответа без "get_">`. Например, `get_device_info` даст
`starlink_device_info_*`.

## Grafana

Дашборд: [grafana_dashboard.json](grafana_dashboard.json). Импорт: Dashboards → New → Import →
Upload JSON file. Источник данных выбирается переменной `Data source` (по умолчанию тот, что
называется `prometheus`), тарелка — переменной `Dish` (лейбл `instance`).

Разделы: Overview (статус, задержка, скорость, препятствия, алерты, версия ПО), Network,
Obstructions, Alignment & GPS, Alerts & readiness, Device & software. Свёрнутые разделы: Power & hardware,
All metrics (таблица всех текущих значений) и Exporter.

## Prometheus

```yaml
scrape_configs:
  - job_name: starlink
    scrape_interval: 3s
    static_configs:
      - targets: ['localhost:9817']
```
