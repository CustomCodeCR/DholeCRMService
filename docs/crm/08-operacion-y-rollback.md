# DholeCRM — Fase 9: operación, seguridad y rollback

> Estado: **implementación parcial, sin aprobación operativa**. Fase 8 permanece pendiente de integración y conciliación. No activar flags ni desplegar a staging/producción desde este PR. El archivo no constituye firma del checklist de aceptación.

## Superficie implementada

- Búsqueda `GET /api/crm/search?q=...` sobre cuentas, leads y oportunidades; solo coincidencias visibles por scopes y asignación. Respuestas con campos `id/kind/title`; nunca retornan cédula, contacto, límites de crédito ni identificadores externos del conector.
- Exportación `GET /api/crm/exports/accounts.csv?q=...&page=1`: exige **ambos** `crm.report.read` y `crm.account.read` o `crm.account.read-all`. Máximo 500 cuentas por página, estable por nombre/UUID y limitado al vendedor autorizado. Sin datos fiscales, consentimientos, email ni crédito. Escapa comillas, saltos de línea y fórmulas potenciales al abrir archivos con Excel.
- Límite `429`: 120 solicitudes/minuto por identidad autenticada (alternativa IP en solicitudes sin identidad) sobre `/api/crm`; no afecta otros módulos. Si esta cuota afecta flujos legítimos, ajustar previa evaluación de carga y seguridad, nunca eliminarla silenciosamente.
- Respuestas CRM con `Cache-Control: no-store`, `Pragma: no-cache`, `X-Content-Type-Options: nosniff` y `Referrer-Policy: no-referrer`.
- Métricas `Dhole.Crm.Http`: contador `dhole.crm.http.requests` e histograma `dhole.crm.http.duration` en milisegundos. Etiquetas exclusivamente método, plantilla de ruta y código HTTP. No exponer públicamente ningún endpoint de métricas; conectar a OpenTelemetry/exporter institucional cuando esté aprobado.

## Verificación reproducible

```bash
dotnet restore DholeCRMService.slnx --configfile NuGet.Config
dotnet build DholeCRMService.slnx -c Release --no-restore --warnaserror
CRM_TEST_POSTGRES='<cadena de pruebas proporcionada externamente>' \
  dotnet test DholeCRMService.slnx -c Release --no-build --logger trx
bash -n scripts/deploy-crm.sh scripts/rollback-crm.sh
python3 scripts/check-config.py
```

El PostgreSQL indicado debe ser una instancia de pruebas aislada; el suite crea registros ficticios. Las pruebas funcionales de red, gRPC, Redis, dependencias, construcción de imágenes y Compose pertenecen al flujo CI existente. Conservar resultados de CI y evidencia de staging, no solo capturas del panel.

### Ensayos de rendimiento (staging exclusivamente)

`scripts/load-test-crm.py` ejecuta búsqueda HTTP autenticada con token tomado **solo** de la variable `CRM_PERF_TOKEN`. Exige `CRM_PERF_STAGING_CONFIRMED=true`; usa límites de concurrencia, timeout, volumen, P95 y ratio de errores. No usar credenciales productivas ni intentar pruebas contra producción. Guardar resultado sin el token ni datos comerciales. Si no hay staging autorizado, el ensayo se documenta **no ejecutado**.

## Checklist de promoción (firma pendiente)

| Control | Evidencia requerida | Estado |
| --- | --- | --- |
| Fases 1–8 integradas secuencialmente | PR aprobados y checks verdes | **PENDIENTE** |
| Mapeos Dataverse y fiscal/Hacienda verificados | Documentos firmados por propietario | **PENDIENTE** |
| Scope + dueño en todos los listados, detalles, exports y RPC | Tests de autorización y revisión | **PARCIAL** |
| Auditoría sin datos sensibles para asignación, crédito, conversión, consentimiento y sincronización | Consulta real AuditLogs | **PENDIENTE** |
| Outbox/Inbox, workers y reinicios idempotentes | Tests de entrega y recuperaciones | **PENDIENTE** |
| Unitarios e integración .NET/PostgreSQL/Redis/gRPC | Workflow completo aprobado | **PENDIENTE** |
| Web E2E por perfil, ES/EN y responsive | Pruebas en staging | **PENDIENTE** |
| Rendimiento búsqueda y p95/error-rate | Ensayo staging acordado | **PENDIENTE** |
| Seguridad y dependencias sin hallazgos altos | Scan, revisión de secretos y acceso | **PENDIENTE** |
| Backup verificado + restauración ensayada | Evidencia DBA, sin datos reales en Git | **PENDIENTE** |
| Compose efectivo, rutas Gateway, Auth/Pricing sin regresión | Smoke test del runner | **PENDIENTE** |
| Aprobación del dueño para promoción a master | Aprobación explícita registrada | **PENDIENTE** |

## Runbook de incidente

1. Identificar commit, entorno, contenedores afectados y correlación del error en logs. Usar códigos, no payloads con cédula, email, crédito ni claves.
2. Congelar nuevas importaciones con `Features__CrmDynamicsSyncEnabled=false`; si hay problemas de recordatorios, desactivar `Features__CrmBackgroundTasksEnabled` según autorización. Preservar `crm_outbox_messages`, `crm_inbox_messages`, jobs, checkpoints, correlaciones y conflictos.
3. Verificar disponibilidad de PostgreSQL y Redis y copia respaldada; **no hacer drop de tablas, revertir migraciones ni borrar nuevos registros**. Documentar discrepancias de Dynamics en una conciliación de solo lectura.
4. Para un fallo estrictamente de aplicación, operar **en el runner autorizado con las rutas ya validadas** y el checkout de DholeSystem correspondiente:
   ```bash
   # Solo el operador aprobado, desde el checkout que contiene scripts/ y _dhole-system/
   CRM_ROLLBACK_APPROVED=true bash scripts/rollback-crm.sh staging
   # En producción: requiere aprobación independiente, no ejecutar por rutina.
   CRM_ROLLBACK_APPROVED=true bash scripts/rollback-crm.sh production
   ```
5. El script exige archivos protegidos, marcadores `{staging|production}-previous.sha`/success, ambas imágenes inmutables disponibles y Compose efectivo con redes correctas. Retaguea y reinicia **solo** `crm-api` y `crm-workers`; verifica readiness y otros servicios; solo entonces actualiza marcadores. Si falla, intenta restaurar el tag activo anterior. No ejecuta comandos SQL ni migraciones.
6. Confirmar nuevamente health API y worker, gRPC y conectividad de Gateway/Auth/Pricing, luego comprobar transacciones diferidas y drift Dynamics antes de reactivar flags. Escalar si las imágenes anteriores no arrancan o el cambio de esquema impide volver al binario anterior: exige plan de compatibilidad y DBA, **nunca rollback automático de BD**.

## Dependencias externas bloqueantes

Faltan metadata/option sets de Dataverse, contrato Hacienda confirmado, acceso real y permisos del conector, conciliación Dynamics de contactos y sincronización incremental, integración de Fases 1–8 y entorno staging aprobado. No hay evidencia de ensayo E2E o carga y **nadie ha firmado aceptación**.
