# DholeCRM Fase 5 — Seguimientos, recordatorios y entrega
Fuente funcional única: `CODEX_DHOLECRM_IMPLEMENTACION_COMPLETA.md`.

Las seis tablas aditivas son `crm_account_followup_policies`, `crm_followups`,
`crm_followup_history`, `crm_reminders`, `crm_outbox_messages`, `crm_inbox_messages`.
Todos los eventos se crean después de confirmar una transacción PostgreSQL.
`crm_reminders.status=Queued` significa que **Redis recibió el evento**, no que se entregó
el email; Notifications mantiene estado final y reintentos del proveedor.

Features__CrmEnabled=false y Features__CrmBackgroundTasksEnabled=false por defecto,
hasta que Fases 1–4 estén integradas y probadas. No auto-migrar ni promocionar sin respaldo
efectivo y revisión del Compose incluido del servidor.
La entrega Redis usa `notifications.notification.requested` a
`dhole.notifications.events` y `eventId` del UUID estable del recordatorio; el
consumidor Notifications reusa ese ID como PK de la notificación, permitiendo
reentrega idempotente tras caída entre publish y commit del outbox.

Política por cuenta con timezone, intervalo, lead time, pausas y canales.
Los avisos van al ejecutivo interno activo de Auth, no a contactos comerciales:
`DoNotContact` y consentimiento marketing se mantienen separados y no se interpretan
como permiso de campaña. Email se resuelve a través del endpoint Auth protegido,
sin persistir tokens ni direcciones en logs de aplicación.
En modo pendiente se revalida estado y versión para evitar avisos de visitas
reprogramadas/completadas; nunca registrar automáticamente una visita como atendida.
El usuario puede reprogramar o cancelar sin borrar historial.

## Controles operativos
- Redis Streams y Notifications workers deben apuntar al **mismo** stream y Redis.
- Consumers de Notifications deben estar en ejecución y saludables.
- Verificar `crm_reminders` y `crm_outbox_messages` Pending/Queued/Failed/DeadLetter
  y consultar Notifications para entrega final.
- Cualquier entrega puede repetirse a nivel Redis: el ID persistente debe evitar
  duplicar filas en Notifications. El proveedor de email no garantiza entrega
  exactamente una vez después de una desconexión externa.
- Si se retira una versión de API/Worker, **no eliminar ni revertir destructivamente**
  las seis tablas; restaurar la imagen anterior y mantener el historial.
