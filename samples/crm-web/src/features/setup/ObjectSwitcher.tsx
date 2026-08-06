import { Button } from '@/design/primitives'
import { OBJECT_MODELS } from '@/fixtures/objects'
import styles from './setup.module.css'

/**
 * Which object a setup screen is editing.
 *
 * Six screens need it, so it lives once. A copy per screen would be six places for the list to
 * fall out of step with the object model that drives every one of them.
 */
export function ObjectSwitcher({
  value,
  onChange,
}: {
  value: string
  onChange: (key: string) => void
}) {
  return (
    <div className={styles.objectStrip} role="group" aria-label="Object">
      {Object.values(OBJECT_MODELS).map((object) => (
        <Button
          key={object.key}
          size="sm"
          pill
          aria-pressed={value === object.key}
          onClick={() => onChange(object.key)}
        >
          {object.plural}
        </Button>
      ))}
    </div>
  )
}
