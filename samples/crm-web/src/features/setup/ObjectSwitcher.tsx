import { Button } from '@/design/primitives'
import { useSchema } from '@/api/queries/hooks'
import { schemaRows } from './schemaModel'
import styles from './setup.module.css'

/**
 * Which object a setup screen is editing.
 *
 * Six screens need it, so it lives once. A copy per screen would be six places for the list to
 * fall out of step.
 *
 * The list is the tenant's, from `describe`. A switcher offering the objects this build was
 * compiled with would be missing the one the administrator declared a minute ago — on the screen
 * they declared it from.
 */
export function ObjectSwitcher({
  value,
  onChange,
}: {
  value: string
  onChange: (key: string) => void
}) {
  const schema = useSchema()

  return (
    <div className={styles.objectStrip} role="group" aria-label="Object">
      {schemaRows(schema.data).map((object) => (
        <Button
          key={object.key}
          size="sm"
          pill
          aria-pressed={value === object.key}
          onClick={() => onChange(object.key)}
        >
          {object.label}
        </Button>
      ))}
    </div>
  )
}
