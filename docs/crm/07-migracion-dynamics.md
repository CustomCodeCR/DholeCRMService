# DholeCRM Fase 8 — importación Dynamics y verificación Hacienda

## Estado real y prerequisitos
El código de esta rama está basado en la Fase 6 todavía no integrada. El PR de DholeWeb Fase 7 está abierto. **No desplegar** hasta integrar y validar las Fases 1–7 por orden. El acceso a Dataverse no puede asumirse de un script local: usar **exclusivamente** el conector Dynamics ya existente y su consulta guardada habilitada y una API key restringida a esa consulta. La consola de Dynamics expone metadata discovery; **no inventar campos**.

## Activación (configuración externa al repositorio, sin secretos en commits)
Los flags `Features__CrmEnabled`, `Features__CrmBackgroundTasksEnabled`,
`Features__CrmDynamicsSyncEnabled` y `Features__CrmHaciendaValidationEnabled`
permanecen false si no están configurados. Para Dynamics suministrar bajo configuración segura:

- `Crm__Dynamics__BaseUrl`: origen HTTPS del conector, sin ruta/query.
- `Crm__Dynamics__IntegrationKey`: key restringida a consulta guardada (nunca al navegador).
- `Crm__Dynamics__ConnectionCode`: código existente aprobado del tenant.
- `Crm__Dynamics__SavedQuery`: consulta guardada **habilitada** sobre la entidad accounts, paginada y con select de campos realmente verificados.
- `Crm__Dynamics__Fields__{LogicalName}`: nombre físico exacto obtenido del metadata discovery, para los once campos del CSV.
- `Crm__Dynamics__ExecutiveMap__{remoteGuid}`: UUID de usuario Auth local aprobado.
- `Crm__Dynamics__CountryMap__{sourceCode}`: UUID país existente en Config.
- `Crm__Dynamics__TaxTypeMap__{sourceCode}`: tipo de identificación reconocido en CRM.
- `Crm__Dynamics__CodeMap__{CustomerType|CategoryType|CategoryCode}__{sourceCode}`: códigos CRM validados.
- `Crm__Dynamics__PollingSeconds`: intervalo 15–300, predeterminado 30.

El mapper exige mapeo para país y ejecutivo; evita coincidencia por nombre. El hash de configuración de cada job incluye mappings y bloquea reanudación ante cambios. No copiar archivos `.env` reales al repositorio.

Para Hacienda (solo identificaciones elegibles de Costa Rica): `Crm__Hacienda__CostaRicaCountryId` (UUID Config),
`Crm__Hacienda__EligibleTaxTypes` (CSV de códigos válidos),
`Crm__Hacienda__ApiUrl` (HTTPS en dominio gubernamental `.go.cr`),
`Crm__Hacienda__IdentificationParameter`, `Crm__Hacienda__NameField` y opcionales
`ActivitiesField`, `ActivityCodeField`, `ActivityDescriptionField`. Los nombres físicos deben verificarse contra el contrato **real** de Hacienda. No se suponen rutas HTTP ni esquemas. La vista CRM no reemplaza razón social introducida manualmente; solo actualiza estado y actividades marcadas `HACIENDA`.

## Protocolo de ejecución

1. Confirmar metadata del tenant/option sets; rellenar cada nombre de `dynamics-field-mapping.csv`, relacionar Auth/Config y obtener aprobaciones de fuente de verdad. Crear saved query restringida a las columnas acordadas, conexión y key con quota.
2. Respaldo PostgreSQL staging. Habilitar CRM y Dynamics **solo staging**; aplicar migración `20261025220000_AddDynamicsSynchronization` con control de lock.
3. Usuario con `crm.integration.manage` llama `POST /api/crm/integrations/dynamics/dry-run`; revisar `wouldCreate/unchanged/conflicts/truncated/reasons`. No modifica tablas de negocio.
4. Tras revisar, enviar `POST /api/crm/integrations/dynamics/run` con `Idempotency-Key` (8–128); responde 202 con job. No procesa dentro de petición HTTP.
5. Worker toma páginas de máximo 100; guarda **cuentas y correlaciones en la misma transacción que el cursor**. Ante cédula existente sin correlación o remote edit, crea conflicto sin mutar la cuenta. Nunca hace last-write-wins.
6. `GET /api/crm/integrations/jobs`, `GET /jobs/{id}`, `GET /jobs/{id}/conflicts` para reconciliar. Errores transitorios se reintentan hasta 5 veces; fallos definitivos conservan cursor. `POST /jobs/{id}/retry` solicita reproceso tras corregir conexión; si cambia mapping, se exige nuevo dry-run.
7. Comprobar conteos de filas, conflictos, referencias, usuario asignado, USD/CRC y consentimiento; sin reconciliación autorizada no habilitar edición bilateral. **No conceder consentimiento marketing por preferencia**.
8. Hacienda: cuentas CR pendientes se verifican solo si país+documento elegibles y flag activo. Si falla proveedor, estado `Failed`, operación manual permitida; `POST /accounts/{id}/hacienda/retry` requiere `crm.account.update` y `expectedVersion`. No llamar Hacienda para países extranjeros.

## Límites verificados de este PR
- Implementa importación inicial solo **Account**. No afirmar migración de contactos, conversiones, actividades, contratos ni sincronización bidireccional: requieren metadata y mappings de Dynamics que no se aportaron.
- El cursor proviene de la consulta guardada. No utiliza directamente `DataverseDeltaReader`; sincronización incremental por delta aún depende de la concesión y contrato de `SyncChangeEndpoints` y de reglas por atributo.
- No se ejecutaron llamadas reales a Dynamics ni Hacienda sin URLs, mapeos y credenciales; los flags quedan desactivados y respuestas no pueden simular éxito.
- Los scripts y acciones de staging/prod no se ejecutan desde este PR. No se toca el Compose del servidor ni las credenciales.

## Rollback
Apagar primero `Features__CrmDynamicsSyncEnabled` y `Features__CrmHaciendaValidationEnabled`; detener workers. Restaurar imagen anterior API/worker si fuera necesario. Exportar `crm_sync_jobs`, `crm_external_mappings`, `crm_sync_conflicts` y cuentas nuevas antes de cualquier plan correctivo. **Nunca borrar esas tablas ni revertir automáticamente la migración**; preservar IDs/relaciones y revisar conflictos con propietario.
